//! Authorization. The real authorizer combines two checks per connection:
//!
//! 1. **logind** — the peer UID must own an active, local, graphical session.
//! 2. **polkit** — the peer process (as a `unix-process` subject) must be
//!    authorized for `io.github.wormforce.Battuta.Input1.subscribe`.
//!
//! Both halves are injected as traits so the decision logic is unit-testable
//! and the mock-bus e2e can drive them without touching the real session
//! state. Every failure mode rejects (`fail closed`), including
//! infrastructure errors; the reason travels to the client in the `Error`
//! frame's message.

use crate::dbus::Connection;
use crate::logind::LogindClient;
use crate::polkit::{Decision as PolkitDecision, PolkitChecker, SUBSCRIBE_ACTION};
use std::fmt;
use std::sync::Arc;

/// Credentials of a connected peer, obtained with SO_PEERCRED.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PeerCredentials {
    pub pid: u32,
    pub uid: u32,
    pub gid: u32,
}

#[derive(Debug)]
pub enum AuthError {
    /// Authorization infrastructure is unavailable; nothing can be trusted.
    Unavailable(String),
    /// The peer is known but not allowed.
    Rejected(String),
}

impl fmt::Display for AuthError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            AuthError::Unavailable(reason) => {
                write!(f, "authorization unavailable: {reason}")
            }
            AuthError::Rejected(reason) => write!(f, "peer not authorized: {reason}"),
        }
    }
}

impl std::error::Error for AuthError {}

/// Decides whether a connecting peer may subscribe to input events.
pub trait Authorizer: Send + Sync + 'static {
    fn authorize(&self, peer: &PeerCredentials) -> Result<(), AuthError>;

    /// Human-facing description used in diagnostics.
    fn describe(&self) -> String;
}

/// Allows any local peer. Dev/test only: it defeats the entire threat model
/// and must never run in a packaged build.
pub struct DevAuthorizer;

impl Authorizer for DevAuthorizer {
    fn authorize(&self, _peer: &PeerCredentials) -> Result<(), AuthError> {
        Ok(())
    }

    fn describe(&self) -> String {
        "dev-mode (authorization disabled)".to_owned()
    }
}

/// Rejects every peer with a fixed reason. Used when the system bus is
/// unreachable so that a broken deployment fails closed instead of open.
pub struct RejectAll {
    pub reason: String,
}

impl Authorizer for RejectAll {
    fn authorize(&self, _peer: &PeerCredentials) -> Result<(), AuthError> {
        Err(AuthError::Unavailable(self.reason.clone()))
    }

    fn describe(&self) -> String {
        "reject-all (authorization unavailable)".to_owned()
    }
}

/// The logind half: which UIDs may even be considered.
pub trait SessionInspector: Send + Sync + 'static {
    fn has_authorized_graphical_session(&self, uid: u32) -> Result<bool, String>;
}

/// The polkit half: which processes may subscribe.
pub trait PrivilegeChecker: Send + Sync + 'static {
    fn check_subscribe(&self, peer: &PeerCredentials) -> Result<PolkitDecision, String>;
}

struct DbusSessionInspector {
    logind: LogindClient,
}

impl SessionInspector for DbusSessionInspector {
    fn has_authorized_graphical_session(&self, uid: u32) -> Result<bool, String> {
        self.logind.has_authorized_graphical_session(uid)
    }
}

struct DbusPrivilegeChecker {
    checker: PolkitChecker,
}

impl PrivilegeChecker for DbusPrivilegeChecker {
    fn check_subscribe(&self, peer: &PeerCredentials) -> Result<PolkitDecision, String> {
        self.checker.check_subscribe(peer)
    }
}

/// The production authorizer: logind session validation + polkit action
/// authorization, re-run on every connection.
pub struct SystemAuthorizer {
    sessions: Arc<dyn SessionInspector>,
    privileges: Arc<dyn PrivilegeChecker>,
}

impl SystemAuthorizer {
    pub fn new(sessions: Arc<dyn SessionInspector>, privileges: Arc<dyn PrivilegeChecker>) -> Self {
        Self {
            sessions,
            privileges,
        }
    }

    /// Builds the real authorizer on the system bus (or an explicit address
    /// override for tests), returning the shared bus connection for the
    /// lifecycle monitor.
    pub fn connect_with_connection(address: Option<&str>) -> Result<(Self, Connection), String> {
        let connection = match address {
            Some(address) => Connection::connect(address)?,
            None => Connection::system()?,
        };
        let authorizer = Self::new(
            Arc::new(DbusSessionInspector {
                logind: LogindClient::new(connection.clone()),
            }),
            Arc::new(DbusPrivilegeChecker {
                checker: PolkitChecker::new(connection.clone()),
            }),
        );
        Ok((authorizer, connection))
    }
}

impl Authorizer for SystemAuthorizer {
    fn authorize(&self, peer: &PeerCredentials) -> Result<(), AuthError> {
        // 1. logind: active, local, graphical session owned by the UID.
        let has_session = self
            .sessions
            .has_authorized_graphical_session(peer.uid)
            .map_err(|error| AuthError::Unavailable(format!("logind query failed: {error}")))?;
        if !has_session {
            return Err(AuthError::Rejected(format!(
                "uid {} owns no active local graphical session",
                peer.uid
            )));
        }

        // 2. polkit: the subscribing process is authorized for the action.
        match self.privileges.check_subscribe(peer) {
            Ok(PolkitDecision::Authorized) => Ok(()),
            Ok(PolkitDecision::Challenge) => Err(AuthError::Rejected(format!(
                "polkit requires interactive authorization for {SUBSCRIBE_ACTION}; \
                 authorize from the Battuta app (its own connection may prompt)"
            ))),
            Ok(PolkitDecision::Denied) => Err(AuthError::Rejected(format!(
                "polkit denied {SUBSCRIBE_ACTION}"
            ))),
            Err(error) => Err(AuthError::Rejected(format!(
                "authorization check failed (fail closed): {error}"
            ))),
        }
    }

    fn describe(&self) -> String {
        format!("logind session + polkit {SUBSCRIBE_ACTION}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    fn peer(pid: u32, uid: u32) -> PeerCredentials {
        PeerCredentials {
            pid,
            uid,
            gid: 1000,
        }
    }

    struct StaticSessions(bool);

    impl SessionInspector for StaticSessions {
        fn has_authorized_graphical_session(&self, uid: u32) -> Result<bool, String> {
            if uid == 0 {
                Err("logind exploded".to_owned())
            } else {
                Ok(self.0)
            }
        }
    }

    struct StaticPrivileges {
        decision: Mutex<PolkitDecision>,
        fail: bool,
    }

    impl PrivilegeChecker for StaticPrivileges {
        fn check_subscribe(&self, _peer: &PeerCredentials) -> Result<PolkitDecision, String> {
            if self.fail {
                return Err("polkitd vanished".to_owned());
            }
            Ok(*self.decision.lock().unwrap())
        }
    }

    fn authorizer(
        has_session: bool,
        decision: PolkitDecision,
        privileges_fail: bool,
    ) -> SystemAuthorizer {
        SystemAuthorizer::new(
            Arc::new(StaticSessions(has_session)),
            Arc::new(StaticPrivileges {
                decision: Mutex::new(decision),
                fail: privileges_fail,
            }),
        )
    }

    #[test]
    fn accepts_session_owner_with_polkit_yes() {
        assert!(
            authorizer(true, PolkitDecision::Authorized, false)
                .authorize(&peer(42, 1000))
                .is_ok()
        );
    }

    #[test]
    fn rejects_uid_without_graphical_session() {
        let error = authorizer(false, PolkitDecision::Authorized, false)
            .authorize(&peer(42, 1000))
            .unwrap_err();
        assert!(
            matches!(error, AuthError::Rejected(ref text) if text.contains("no active local graphical session")),
            "unexpected error: {error:?}"
        );
    }

    #[test]
    fn rejects_polkit_denial_and_challenge() {
        for decision in [PolkitDecision::Denied, PolkitDecision::Challenge] {
            let error = authorizer(true, decision, false)
                .authorize(&peer(42, 1000))
                .unwrap_err();
            assert!(
                matches!(error, AuthError::Rejected(_)),
                "{decision:?} must reject: {error:?}"
            );
        }
    }

    #[test]
    fn infrastructure_failures_fail_closed() {
        // logind query failure.
        let error = authorizer(true, PolkitDecision::Authorized, false)
            .authorize(&peer(42, 0))
            .unwrap_err();
        assert!(matches!(error, AuthError::Unavailable(_)), "{error:?}");

        // polkit failure.
        let error = authorizer(true, PolkitDecision::Authorized, true)
            .authorize(&peer(42, 1000))
            .unwrap_err();
        assert!(
            matches!(error, AuthError::Rejected(ref text) if text.contains("fail closed")),
            "{error:?}"
        );
    }

    #[test]
    fn reject_all_reports_unavailability() {
        let error = RejectAll {
            reason: "no system bus".to_owned(),
        }
        .authorize(&peer(1, 1000))
        .unwrap_err();
        assert!(
            matches!(error, AuthError::Unavailable(reason) if reason.contains("no system bus"))
        );
    }

    #[test]
    fn dev_authorizer_accepts_everything() {
        assert!(DevAuthorizer.authorize(&peer(1, 1000)).is_ok());
        assert_eq!(
            DevAuthorizer.describe(),
            "dev-mode (authorization disabled)"
        );
    }
}
