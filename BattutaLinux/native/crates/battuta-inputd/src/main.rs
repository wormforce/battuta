//! battuta-inputd entry point. See BattutaLinux/README.md for usage; PR 4
//! runs only the synthetic event source (--synthetic).

use battuta_inputd::server::{self, ServerConfig};
use battuta_inputd::synthetic::SyntheticConfig;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

const USAGE: &str = "\
battuta-inputd — Battuta system input broker

USAGE:
    battuta-inputd [OPTIONS]

OPTIONS:
    --socket-path <PATH>       Socket path (default /run/battuta-inputd/input.sock);
                               ignored under systemd socket activation,
                               which hands over the listener at fd 3
    --socket-mode <MODE>       Socket file mode in octal (default 0660)
    --socket-group <NAME|GID>  Group the socket node is chowned to
    --synthetic                Serve the deterministic synthetic event script
                               instead of real evdev input
    --synthetic-interval-ms N  Pause between synthetic steps (default 100)
    --synthetic-once           Run the synthetic script once per subscriber
                               instead of looping
    --devices-dir <PATH>       Directory scanned for evdev devices
                               (default /dev/input)
    --no-vt-gate               Disable the session gate that pauses delivery
                               while the graphical VT is inactive
    --no-lock-gate             Disable the lock-screen/sleep gate driven by
                               logind signals
    --no-bus-name              Do not claim io.github.wormforce.Battuta.Input1
                               (development/test runs)
    --graphical-vtnr N         VT number of the graphical session; defaults to
                               auto-detection from logind session records
    --system-bus-address ADDR  D-Bus system bus address override (tests and
                               nonstandard deployments)
    --heartbeat-ms N           Offer heartbeats every N ms in hello_ack
                               (default 0 = disabled)
    --seat <NAME>              Seat name reported in hello_ack (default seat0)
    --dev-auth                 Allow any local peer without logind/polkit
                               (development/test only)
    --handshake-timeout-ms N   Client handshake deadline (default 5000)
    -h, --help                 Print this help
";

struct Options {
    config: ServerConfig,
}

fn take_value(items: &[String], name: &str, index: &mut usize) -> Result<String, String> {
    *index += 1;
    items
        .get(*index)
        .cloned()
        .ok_or_else(|| format!("{name} requires a value"))
}

fn parse_args(args: impl Iterator<Item = String>) -> Result<Options, String> {
    let mut config = ServerConfig::default();
    let mut synthetic = false;
    let mut synthetic_interval_ms = 100_u64;
    let mut synthetic_loop = true;

    let items: Vec<String> = args.collect();
    let mut index = 0;
    while index < items.len() {
        match items[index].as_str() {
            "--socket-path" => {
                config.socket_path =
                    PathBuf::from(take_value(&items, "--socket-path", &mut index)?);
            }
            "--socket-mode" => {
                let value = take_value(&items, "--socket-mode", &mut index)?;
                config.socket_mode = battuta_inputd::perms::parse_mode(&value)?;
            }
            "--socket-group" => {
                config.socket_group = Some(take_value(&items, "--socket-group", &mut index)?);
            }
            "--synthetic" => synthetic = true,
            "--synthetic-interval-ms" => {
                synthetic_interval_ms = take_value(&items, "--synthetic-interval-ms", &mut index)?
                    .parse()
                    .map_err(|_| "--synthetic-interval-ms must be a number")?;
            }
            "--synthetic-once" => synthetic_loop = false,
            "--devices-dir" => {
                config.devices_dir =
                    PathBuf::from(take_value(&items, "--devices-dir", &mut index)?);
            }
            "--no-vt-gate" => config.vt_gate.enabled = false,
            "--no-lock-gate" => config.lock_gate = false,
            "--no-bus-name" => config.claim_bus_name = false,
            "--system-bus-address" => {
                config.system_bus_address =
                    Some(take_value(&items, "--system-bus-address", &mut index)?);
            }
            "--graphical-vtnr" => {
                let value: u32 = take_value(&items, "--graphical-vtnr", &mut index)?
                    .parse()
                    .map_err(|_| "--graphical-vtnr must be a number")?;
                config.vt_gate.graphical_vtnr = value;
            }
            "--heartbeat-ms" => {
                config.heartbeat_interval_ms = take_value(&items, "--heartbeat-ms", &mut index)?
                    .parse()
                    .map_err(|_| "--heartbeat-ms must be a number")?;
            }
            "--seat" => {
                config.seat = take_value(&items, "--seat", &mut index)?;
            }
            "--dev-auth" => config.dev_auth = true,
            "--handshake-timeout-ms" => {
                let value: u64 = take_value(&items, "--handshake-timeout-ms", &mut index)?
                    .parse()
                    .map_err(|_| "--handshake-timeout-ms must be a number")?;
                config.handshake_timeout = Duration::from_millis(value);
            }
            "-h" | "--help" => return Err(USAGE.to_owned()),
            other => return Err(format!("unknown option: {other}\n\n{USAGE}")),
        }

        index += 1;
    }

    if synthetic {
        config.synthetic = Some(SyntheticConfig {
            interval: Duration::from_millis(synthetic_interval_ms),
            loop_forever: synthetic_loop,
        });
    }

    Ok(Options { config })
}

fn main() -> ExitCode {
    let options = match parse_args(std::env::args().skip(1)) {
        Ok(options) => options,
        Err(usage) => {
            eprintln!("{usage}");
            return ExitCode::from(2);
        }
    };

    match server::run(options.config) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("battuta-inputd: fatal: {error}");
            ExitCode::FAILURE
        }
    }
}
