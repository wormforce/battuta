//! systemd socket activation (Linux port PR 10).
//!
//! The packaged broker is an on-demand system service (plan §5.2/§9.2): the
//! `battuta-inputd.socket` unit owns the listening SEQPACKET node and starts
//! the service on the first client connection. The handoff follows the
//! standard `sd_listen_fds(3)` protocol: `LISTEN_FDS`/`LISTEN_PID` in the
//! environment and the pre-bound listener at descriptor 3. When the broker
//! is started manually (dev runs, the demo recipe), the variables are absent
//! and the server binds `--socket-path` itself as before.

use std::env;
use std::io;
use std::os::fd::{FromRawFd, OwnedFd};
use std::process;

/// What [`acquire`] found in the environment.
pub enum Activation {
    /// No socket activation variables: the server binds its own socket.
    None,
    /// Exactly one pre-bound listening socket handed over by systemd.
    Listener(OwnedFd),
}

/// Outcome of the pure environment decision, kept free of fd side effects
/// so tests can exercise every branch.
enum Plan {
    None,
    /// The first of `count` activated fds belongs to us (descriptor 3).
    Activated,
}

/// Decides whether this process was socket-activated. Mirrors
/// `sd_listen_fds(3)`: a missing or `LISTEN_PID`-mismatched environment is
/// inherited noise, not an error; an fd count we cannot serve is.
fn plan(listen_pid: Option<&str>, listen_fds: Option<&str>, own_pid: u32) -> Result<Plan, String> {
    let Some(raw_fds) = listen_fds else {
        return Ok(Plan::None);
    };
    let count: usize = raw_fds
        .trim()
        .parse()
        .map_err(|_| format!("LISTEN_FDS is not a number: {raw_fds:?}"))?;
    if count == 0 {
        return Ok(Plan::None);
    }

    match listen_pid {
        Some(raw_pid) => match raw_pid.trim().parse::<u32>() {
            Ok(pid) if pid == own_pid => {}
            _ => {
                // The variables were inherited from a parent process (e.g.
                // a wrapper started the broker from a shell that had them).
                return Ok(Plan::None);
            }
        },
        None => return Ok(Plan::None),
    }

    if count != 1 {
        return Err(format!(
            "battuta-inputd serves exactly one activated socket, LISTEN_FDS={count}"
        ));
    }

    Ok(Plan::Activated)
}

/// Reads the activation environment and, when activated, takes ownership of
/// descriptor 3. Misconfigured environments fail loudly instead of letting
/// the server silently bind a second socket.
pub fn acquire() -> io::Result<Activation> {
    let decision = plan(
        env::var("LISTEN_PID").ok().as_deref(),
        env::var("LISTEN_FDS").ok().as_deref(),
        process::id(),
    )
    .map_err(io::Error::other)?;

    match decision {
        Plan::None => Ok(Activation::None),
        Plan::Activated => {
            // SAFETY: descriptor 3 is the first systemd-passed fd per the
            // sd_listen_fds contract (LISTEN_PID matched our pid above); we
            // take sole ownership and never duplicate or close it elsewhere.
            let fd = unsafe { OwnedFd::from_raw_fd(3) };
            Ok(Activation::Listener(fd))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn without_environment_binds_own_socket() {
        assert!(matches!(plan(None, None, 4242), Ok(Plan::None)));
        assert!(matches!(plan(None, Some("1"), 4242), Ok(Plan::None)));
        assert!(matches!(
            plan(Some("4242"), Some("0"), 4242),
            Ok(Plan::None)
        ));
    }

    #[test]
    fn inherited_variables_are_ignored_not_errors() {
        // A parent process (PID mismatch) passed its own variables down.
        assert!(matches!(plan(Some("1"), Some("1"), 4242), Ok(Plan::None)));
        assert!(matches!(plan(Some("x"), Some("1"), 4242), Ok(Plan::None)));
    }

    #[test]
    fn matching_pid_activates() {
        assert!(matches!(
            plan(Some("4242"), Some("1"), 4242),
            Ok(Plan::Activated)
        ));
        // Tolerate surrounding whitespace like sd_listen_fds does.
        assert!(matches!(
            plan(Some(" 4242 "), Some(" 1 "), 4242),
            Ok(Plan::Activated)
        ));
    }

    #[test]
    fn unexpected_fd_counts_and_garbage_fail_loudly() {
        assert!(plan(Some("4242"), Some("2"), 4242).is_err());
        assert!(plan(Some("4242"), Some("12"), 4242).is_err());
        assert!(plan(Some("4242"), Some("many"), 4242).is_err());
    }
}
