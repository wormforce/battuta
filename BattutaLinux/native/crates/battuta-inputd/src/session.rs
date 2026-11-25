//! Session gating: pause event delivery while the graphical VT is inactive.
//!
//! The plan (§5.2) requires the broker to pause on VT switches, lock screens,
//! and user switches. This module implements the VT-switch half: it watches
//! the kernel's "currently active VT" sysfs node and drives the hub gate.
//! Lock-screen and logind-signal integration (PrepareForSleep, Lock/Unlock,
//! polkit) arrive with the D-Bus work in the next increment; the gate trait
//! boundary this module establishes is where they plug in.
//!
//! The graphical VT number is auto-detected from logind's session records
//! (an `ACTIVE=1` user session of type `wayland`/`x11`) and can be overridden
//! with `--graphical-vtnr` for deterministic tests and non-logind setups.

use crate::gate::{GateContributor, GateCoordinator};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

/// Where the kernel reports the active VT of `tty0`.
pub const ACTIVE_VT_PATH: &str = "/sys/class/tty/tty0/active";
/// Default logind session record directory.
pub const SESSIONS_DIR: &str = "/run/systemd/sessions";
/// How often the active VT is sampled. sysfs does not support inotify
/// notifications for attribute writes, so this is a poll, kept cheap (one
/// small read per interval).
pub const POLL_INTERVAL: Duration = Duration::from_millis(250);

/// Gate configuration.
#[derive(Clone, Debug)]
pub struct VtGateConfig {
    pub enabled: bool,
    pub active_vt_path: PathBuf,
    /// VT number of the graphical session; delivery pauses while another VT
    /// is active.
    pub graphical_vtnr: u32,
    pub sessions_dir: PathBuf,
}

impl VtGateConfig {
    /// Builds the config with auto-detected graphical VT (falling back to
    /// VT 1, the most common graphical seat assignment) or an explicit
    /// override.
    pub fn detect(override_vtnr: Option<u32>) -> Self {
        let graphical_vtnr = override_vtnr
            .or_else(|| detect_graphical_vtnr(Path::new(SESSIONS_DIR)))
            .unwrap_or(1);
        Self {
            enabled: true,
            active_vt_path: PathBuf::from(ACTIVE_VT_PATH),
            graphical_vtnr,
            sessions_dir: PathBuf::from(SESSIONS_DIR),
        }
    }
}

/// Reads the active VT number from a sysfs "ttyN" node.
pub fn read_active_vtnr(path: &Path) -> Option<u32> {
    let text = std::fs::read_to_string(path).ok()?;
    let text = text.trim();
    let vtnr = text.strip_prefix("tty")?;
    vtnr.parse().ok()
}

/// Finds the VT of the active graphical user session from logind session
/// records. Records are `KEY=VALUE` lines; logind marks the file as private
/// data, so this is a best-effort probe with a conservative fallback — never
/// a security control (authorization stays with logind/polkit).
pub fn detect_graphical_vtnr(sessions_dir: &Path) -> Option<u32> {
    let entries = std::fs::read_dir(sessions_dir).ok()?;
    let mut fallback = None;
    for entry in entries.flatten() {
        let Ok(text) = std::fs::read_to_string(entry.path()) else {
            continue;
        };
        let mut fields = parse_session_record(&text);
        let is_graphical_user = matches!(
            fields.get("TYPE").map(String::as_str),
            Some("wayland") | Some("x11")
        ) && fields.get("CLASS").map(String::as_str) == Some("user");
        if !is_graphical_user {
            continue;
        }
        let Ok(vtnr) = fields.remove("VTNR").unwrap_or_default().parse::<u32>() else {
            continue;
        };
        if fields.get("ACTIVE").map(String::as_str) == Some("1") {
            return Some(vtnr);
        }
        fallback = fallback.or(Some(vtnr));
    }
    fallback
}

fn parse_session_record(text: &str) -> std::collections::HashMap<String, String> {
    let mut fields = std::collections::HashMap::new();
    for line in text.lines() {
        if line.starts_with('#') {
            continue;
        }
        if let Some((key, value)) = line.split_once('=') {
            fields.insert(key.trim().to_owned(), value.trim().to_owned());
        }
    }
    fields
}

/// Spawns the VT gate thread. The initial state was already decided
/// synchronously by the caller (server setup) via the gate coordinator, so a
/// broker started on the wrong VT never delivers; this thread only maintains
/// the VT vote across switches.
pub fn spawn_monitor(
    coordinator: Arc<GateCoordinator>,
    config: VtGateConfig,
) -> std::io::Result<()> {
    if !config.enabled {
        return Ok(());
    }
    std::thread::Builder::new()
        .name("vt-gate".to_owned())
        .spawn(move || gate_loop(coordinator, config))?;
    Ok(())
}

fn gate_loop(coordinator: Arc<GateCoordinator>, config: VtGateConfig) {
    let mut last_open: Option<bool> = None;
    loop {
        let open = read_active_vtnr(&config.active_vt_path) == Some(config.graphical_vtnr);
        if last_open != Some(open) {
            coordinator.set(GateContributor::Vt, open);
            last_open = Some(open);
            eprintln!(
                "battuta-inputd: session gate {} (graphical VT {}, active {})",
                if open { "opened" } else { "closed" },
                config.graphical_vtnr,
                read_active_vtnr(&config.active_vt_path)
                    .map(|v| v.to_string())
                    .unwrap_or_else(|| "unknown".to_owned())
            );
        }
        std::thread::sleep(POLL_INTERVAL);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn reads_active_vt_from_sysfs_format() {
        let dir = std::env::temp_dir().join(format!("battuta-gate-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("active");
        fs::write(&path, "tty1\n").unwrap();
        assert_eq!(read_active_vtnr(&path), Some(1));
        fs::write(&path, "tty42").unwrap();
        assert_eq!(read_active_vtnr(&path), Some(42));
        fs::write(&path, "sda1").unwrap();
        assert_eq!(read_active_vtnr(&path), None);
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn detects_active_graphical_session() {
        let dir = std::env::temp_dir().join(format!("battuta-gate-sess-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        fs::write(
            dir.join("3"),
            "# This is private data. Do not parse.\nUID=1000\nACTIVE=1\nSTATE=active\n\
             TYPE=wayland\nCLASS=user\nSEAT=seat0\nVTNR=1\n",
        )
        .unwrap();
        fs::write(
            dir.join("9"),
            "UID=1000\nACTIVE=0\nTYPE=wayland\nCLASS=user\nVTNR=5\n",
        )
        .unwrap();
        fs::write(
            dir.join("2"),
            "UID=0\nACTIVE=1\nTYPE=tty\nCLASS=user\nVTNR=6\n",
        )
        .unwrap();
        assert_eq!(detect_graphical_vtnr(&dir), Some(1));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn falls_back_to_inactive_graphical_session() {
        let dir = std::env::temp_dir().join(format!("battuta-gate-fb-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        fs::write(
            dir.join("9"),
            "UID=1000\nACTIVE=0\nTYPE=x11\nCLASS=user\nVTNR=5\n",
        )
        .unwrap();
        assert_eq!(detect_graphical_vtnr(&dir), Some(5));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn missing_sessions_dir_is_none() {
        assert_eq!(
            detect_graphical_vtnr(Path::new("/nonexistent-battuta-sessions")),
            None
        );
    }
}
