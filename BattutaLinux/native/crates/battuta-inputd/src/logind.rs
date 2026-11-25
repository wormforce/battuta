//! logind integration: session validation for authorization and the
//! lock-screen / sleep lifecycle gate.
//!
//! - `LogindClient` maps a peer UID to its sessions (`ListSessions` +
//!   `Properties.GetAll`) and applies the plan §5.2 rule: an authorized peer
//!   UID owns an **active, local, graphical** session.
//! - `spawn_lifecycle_monitor` subscribes to `PrepareForSleep` (manager) and
//!   the session `Lock`/`Unlock` signals and votes on the shared
//!   [`GateCoordinator`].

use crate::dbus::{Connection, Endpoint, Value};
use crate::gate::{GateContributor, GateCoordinator};
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

pub const LOGIN_SERVICE: &str = "org.freedesktop.login1";
pub const MANAGER_PATH: &str = "/org/freedesktop/login1";
pub const MANAGER_IFACE: &str = "org.freedesktop.login1.Manager";
pub const SESSION_IFACE: &str = "org.freedesktop.login1.Session";

/// How long the monitor waits between signal checks; only bounds how quickly
/// the process notices a closed connection, signals themselves wake it.
const MONITOR_POLL: Duration = Duration::from_millis(250);

/// One logind session, from `ListSessions` (`a(susso)`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SessionSummary {
    pub id: String,
    pub uid: u32,
    pub user_name: String,
    pub seat: String,
    pub path: String,
}

/// Session properties the authorization decision needs.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SessionProperties {
    pub active: bool,
    pub remote: bool,
    pub class: String,
    pub session_type: String,
}

impl SessionProperties {
    /// Plan §5.2: the session must be active, local, and graphical.
    /// Class `lock-screen` sessions never count (they belong to the same
    /// UID as the real session, which pause delivery via the Lock signal).
    pub fn is_authorized_kind(&self) -> bool {
        self.active
            && !self.remote
            && self.class == "user"
            && matches!(self.session_type.as_str(), "x11" | "wayland" | "mir")
    }
}

/// logind manager/session queries over a shared system-bus connection.
pub struct LogindClient {
    connection: Connection,
    service: String,
}

impl LogindClient {
    pub fn new(connection: Connection) -> Self {
        Self {
            connection,
            service: LOGIN_SERVICE.to_owned(),
        }
    }

    /// Overrides the service name (mock-bus tests only).
    pub fn with_service(mut self, service: &str) -> Self {
        self.service = service.to_owned();
        self
    }

    /// Lists all sessions, mapped from the `a(susso)` reply.
    pub fn list_sessions(&self) -> Result<Vec<SessionSummary>, String> {
        let reply = self
            .connection
            .call(
                Endpoint {
                    destination: &self.service,
                    path: MANAGER_PATH,
                    interface: MANAGER_IFACE,
                    member: "ListSessions",
                },
                Vec::new(),
                "",
                Duration::from_secs(15),
            )
            .map_err(|error| format!("ListSessions failed: {error}"))?;
        let array = reply
            .first()
            .ok_or_else(|| "ListSessions returned no value".to_owned())?;
        let (_, items) = array
            .as_array()
            .ok_or_else(|| "ListSessions reply is not an array".to_owned())?;
        let mut sessions = Vec::with_capacity(items.len());
        for item in items {
            let fields = item
                .as_struct()
                .ok_or_else(|| "ListSessions entry is not a struct".to_owned())?;
            if fields.len() != 5 {
                return Err(format!(
                    "ListSessions entry has {} fields, expected 5",
                    fields.len()
                ));
            }
            sessions.push(SessionSummary {
                id: fields[0].as_str().unwrap_or_default().to_owned(),
                uid: fields[1].as_u32().unwrap_or(0),
                user_name: fields[2].as_str().unwrap_or_default().to_owned(),
                seat: fields[3].as_str().unwrap_or_default().to_owned(),
                path: fields[4].as_str().unwrap_or_default().to_owned(),
            });
        }
        Ok(sessions)
    }

    /// Fetches `org.freedesktop.login1.Session` properties for one session.
    pub fn session_properties(&self, path: &str) -> Result<SessionProperties, String> {
        let reply = self
            .connection
            .call(
                Endpoint {
                    destination: &self.service,
                    path,
                    interface: "org.freedesktop.DBus.Properties",
                    member: "GetAll",
                },
                vec![Value::str(SESSION_IFACE)],
                "s",
                Duration::from_secs(15),
            )
            .map_err(|error| format!("GetAll failed for {path}: {error}"))?;
        let array = reply
            .first()
            .ok_or_else(|| "GetAll returned no value".to_owned())?;
        let (_, items) = array
            .as_array()
            .ok_or_else(|| "GetAll reply is not an a{sv}".to_owned())?;
        let mut props: HashMap<&str, &Value> = HashMap::new();
        for entry in items {
            let Some((key, value)) = entry.as_dict_entry() else {
                continue;
            };
            let Some(key) = key.as_str() else { continue };
            // Properties arrive as variants; unwrap for the scalars we need.
            let value = match value {
                Value::Variant(inner) => inner,
                other => other,
            };
            props.insert(key, value);
        }
        Ok(SessionProperties {
            active: props
                .get("Active")
                .and_then(|v| v.as_bool())
                .unwrap_or(false),
            remote: props
                .get("Remote")
                .and_then(|v| v.as_bool())
                .unwrap_or(true),
            class: props
                .get("Class")
                .and_then(|v| v.as_str())
                .unwrap_or_default()
                .to_owned(),
            session_type: props
                .get("Type")
                .and_then(|v| v.as_str())
                .unwrap_or_default()
                .to_owned(),
        })
    }

    /// Plan §5.2 authorization rule: does `uid` own an active, local,
    /// graphical session?
    pub fn has_authorized_graphical_session(&self, uid: u32) -> Result<bool, String> {
        let sessions = self.list_sessions()?;
        eprintln!(
            "battuta-inputd: logind lists {} session(s), checking uid {uid}",
            sessions.len()
        );
        for session in sessions {
            if session.uid != uid {
                continue;
            }
            let properties = self.session_properties(&session.path)?;
            eprintln!(
                "battuta-inputd: session {} uid {uid}: active={} remote={} class={:?} type={:?} -> authorized={}",
                session.id,
                properties.active,
                properties.remote,
                properties.class,
                properties.session_type,
                properties.is_authorized_kind()
            );
            if properties.is_authorized_kind() {
                return Ok(true);
            }
        }
        Ok(false)
    }

    /// Every session of `uid` with its properties (diagnostics).
    pub fn sessions_for_uid(
        &self,
        uid: u32,
    ) -> Result<Vec<(SessionSummary, SessionProperties)>, String> {
        let mut result = Vec::new();
        for session in self.list_sessions()? {
            if session.uid != uid {
                continue;
            }
            let properties = self.session_properties(&session.path)?;
            result.push((session, properties));
        }
        Ok(result)
    }
}

/// Registers match rules for the lifecycle signals.
fn add_lifecycle_matches(connection: &Connection, service: &str) -> Result<(), String> {
    connection
        .add_match(&format!(
            "type='signal',sender='{service}',interface='{MANAGER_IFACE}',member='PrepareForSleep'"
        ))
        .map_err(|error| format!("AddMatch PrepareForSleep: {error}"))?;
    // No member filter: Lock and Unlock (plus harmless device signals)
    // arrive and are filtered locally by member name.
    connection
        .add_match(&format!(
            "type='signal',sender='{service}',interface='{SESSION_IFACE}'"
        ))
        .map_err(|error| format!("AddMatch Session signals: {error}"))?;
    Ok(())
}

/// Spawns the lock/sleep monitor thread. It votes on the shared gate
/// coordinator and exits when the connection closes.
pub fn spawn_lifecycle_monitor(
    connection: Connection,
    coordinator: Arc<GateCoordinator>,
) -> Result<(), String> {
    add_lifecycle_matches(&connection, LOGIN_SERVICE)?;
    std::thread::Builder::new()
        .name("logind-lifecycle".to_owned())
        .spawn(move || monitor_loop(connection, coordinator))
        .map_err(|error| format!("spawn logind lifecycle monitor: {error}"))?;
    Ok(())
}

fn monitor_loop(connection: Connection, coordinator: Arc<GateCoordinator>) {
    loop {
        let Some(message) = connection.next_message(MONITOR_POLL) else {
            if connection.is_closed() {
                eprintln!("battuta-inputd: logind lifecycle monitor exiting (connection closed)");
                return;
            }
            continue;
        };
        let (Some(interface), Some(member)) = (&message.interface, &message.member) else {
            continue;
        };
        // The match rules already pin the sender (the bus delivers signals
        // with the sender's unique name, not its well-known name), so no
        // extra filter is needed here.
        match (interface.as_str(), member.as_str()) {
            (MANAGER_IFACE, "PrepareForSleep") => {
                let sleeping = message
                    .values
                    .first()
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                coordinator.set(GateContributor::Sleep, !sleeping);
            }
            (SESSION_IFACE, "Lock") => {
                coordinator.set(GateContributor::Lock, false);
            }
            (SESSION_IFACE, "Unlock") => {
                coordinator.set(GateContributor::Lock, true);
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_kind_rule_matches_plan() {
        let make = |active: bool, remote: bool, class: &str, ty: &str| SessionProperties {
            active,
            remote,
            class: class.to_owned(),
            session_type: ty.to_owned(),
        };
        assert!(make(true, false, "user", "wayland").is_authorized_kind());
        assert!(make(true, false, "user", "x11").is_authorized_kind());
        assert!(
            !make(false, false, "user", "wayland").is_authorized_kind(),
            "inactive"
        );
        assert!(
            !make(true, true, "user", "wayland").is_authorized_kind(),
            "remote"
        );
        assert!(
            !make(true, false, "greeter", "wayland").is_authorized_kind(),
            "greeter"
        );
        assert!(
            !make(true, false, "lock-screen", "wayland").is_authorized_kind(),
            "lock-screen"
        );
        assert!(
            !make(true, false, "user", "tty").is_authorized_kind(),
            "tty is not graphical"
        );
        assert!(
            !make(true, false, "user", "").is_authorized_kind(),
            "unknown type"
        );
    }
}
