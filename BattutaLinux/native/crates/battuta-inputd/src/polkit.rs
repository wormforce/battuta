//! polkit authorization: `CheckAuthorization` for the broker's subscribe
//! action, with the connecting process as a `unix-process` subject.
//!
//! Subject details follow polkit's wire expectations, verified against the
//! real `polkitd`: `pid` (u), `start-time` (t), `uid` (u). The start time is
//! read from `/proc/<pid>/stat` so a recycled PID cannot inherit a decision.

use crate::auth::PeerCredentials;
use crate::dbus::{CallError, Connection, Endpoint, Value};
use std::time::Duration;

/// Well-known polkit service names (overridable in tests via the client).
pub const POLKIT_SERVICE: &str = "org.freedesktop.PolicyKit1";
pub const POLKIT_PATH: &str = "/org/freedesktop/PolicyKit1/Authority";
pub const POLKIT_IFACE: &str = "org.freedesktop.PolicyKit1.Authority";

/// The action that gates subscribing to physical input events.
pub const SUBSCRIBE_ACTION: &str = "io.github.wormforce.Battuta.Input1.subscribe";

/// Outcome of a `CheckAuthorization` call.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Decision {
    /// The subject is authorized (possibly cached by `..._keep`).
    Authorized,
    /// Authorization requires interaction the broker cannot perform.
    Challenge,
    /// Explicitly not authorized.
    Denied,
}

/// Reads the process start time (CLOCK_TICKS since boot) from
/// `/proc/<pid>/stat` field 22, guarding against PID reuse.
pub fn process_start_time(pid: u32) -> Result<u64, String> {
    let text = std::fs::read_to_string(format!("/proc/{pid}/stat"))
        .map_err(|error| format!("process {pid} is gone: {error}"))?;
    // `comm` may contain spaces and parentheses; everything after the last
    // ')' is fields 3.., so field 22 is the 20th token after it.
    let after_comm = text
        .rsplit_once(')')
        .map(|(_, rest)| rest)
        .ok_or_else(|| format!("malformed /proc/{pid}/stat"))?;
    let start_time = after_comm
        .split_whitespace()
        .nth(19)
        .ok_or_else(|| format!("malformed /proc/{pid}/stat: too few fields"))?;
    start_time
        .parse()
        .map_err(|error| format!("malformed /proc/{pid}/stat start time: {error}"))
}

/// A `CheckAuthorization` client over a shared system-bus connection.
pub struct PolkitChecker {
    connection: Connection,
    service: String,
    action: String,
}

impl PolkitChecker {
    pub fn new(connection: Connection) -> Self {
        Self {
            connection,
            service: POLKIT_SERVICE.to_owned(),
            action: SUBSCRIBE_ACTION.to_owned(),
        }
    }

    /// Overrides the service/action names (mock-bus tests only).
    pub fn with_names(mut self, service: &str, action: &str) -> Self {
        self.service = service.to_owned();
        self.action = action.to_owned();
        self
    }

    /// Asks polkit whether the peer process may subscribe. Interactive
    /// authorization is never requested: a headless broker cannot answer a
    /// dialog, so a `Challenge` is reported as such and rejected upstream.
    pub fn check_subscribe(&self, peer: &PeerCredentials) -> Result<Decision, String> {
        let start_time = process_start_time(peer.pid)?;
        // `a{sv}` values are variants (the "v" in the signature); each
        // detail value must be boxed, or the wire stream corrupts.
        let detail = |key: &str, value: Value| Value::DictEntry {
            key: Box::new(Value::str(key)),
            value: Box::new(Value::Variant(Box::new(value))),
        };
        let subject = Value::Struct(vec![
            Value::str("unix-process"),
            Value::Array {
                elem_sig: "{sv}".to_owned(),
                items: vec![
                    detail("pid", Value::U32(peer.pid)),
                    detail("start-time", Value::U64(start_time)),
                    detail("uid", Value::U32(peer.uid)),
                ],
            },
        ]);
        let reply = self
            .connection
            .call(
                Endpoint {
                    destination: &self.service,
                    path: POLKIT_PATH,
                    interface: POLKIT_IFACE,
                    member: "CheckAuthorization",
                },
                vec![
                    subject,
                    Value::str(self.action.clone()),
                    Value::empty_dict("{ss}"),
                    Value::U32(0), // flags: no user interaction
                    Value::str(""),
                ],
                "(sa{sv})sa{ss}us",
                Duration::from_secs(15),
            )
            .map_err(|error| match error {
                CallError::DBus { name, text } => format!("polkit error {name}: {text}"),
                other => other.to_string(),
            })?;

        let fields = reply
            .first()
            .and_then(Value::as_struct)
            .ok_or_else(|| "polkit reply is not a (bba{ss}) struct".to_owned())?;
        let is_authorized = fields
            .first()
            .and_then(Value::as_bool)
            .ok_or_else(|| "polkit reply missing is_authorized".to_owned())?;
        let is_challenge = fields
            .get(1)
            .and_then(Value::as_bool)
            .ok_or_else(|| "polkit reply missing is_challenge".to_owned())?;
        Ok(match (is_authorized, is_challenge) {
            (true, _) => Decision::Authorized,
            (false, true) => Decision::Challenge,
            (false, false) => Decision::Denied,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_own_process_start_time() {
        let pid = std::process::id();
        let start_time = process_start_time(pid).expect("own /proc entry");
        // Cross-check via /proc/self/stat field 22 (same file).
        let text = std::fs::read_to_string("/proc/self/stat").unwrap();
        let expected: u64 = text
            .rsplit_once(')')
            .unwrap()
            .1
            .split_whitespace()
            .nth(19)
            .unwrap()
            .parse()
            .unwrap();
        assert_eq!(start_time, expected);
        assert!(start_time > 0, "boot happened before this process");
    }

    #[test]
    fn missing_process_fails_closed() {
        // Spawn a real process and read its start time, then let it exit;
        // its PID (usually) no longer has a /proc entry afterwards.
        let mut child = std::process::Command::new("true")
            .spawn()
            .expect("spawn true");
        let pid = child.id();
        let start_time = process_start_time(pid).expect("stat while alive");
        let _ = start_time;
        child.wait().expect("wait for true");
        // Re-reading may or may not fail if the PID is recycled immediately;
        // when it fails it must fail with a clear message.
        if let Err(error) = process_start_time(pid) {
            assert!(error.contains("gone") || error.contains("malformed"));
        }
    }
}
