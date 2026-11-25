//! Gate coordination: several independent monitors (VT switch, screen lock,
//! system sleep) each vote on whether delivery is open; the hub gate reflects
//! the conjunction.
//!
//! Without a coordinator, two monitor threads writing `hub.set_gate` directly
//! would race: the VT loop's periodic "open" would override a lock-screen
//! "close". Each contributor owns exactly one boolean, and every change
//! recomputes the combined state — so gates compose in any order and every
//! transition is idempotent.

use crate::hub::Hub;
use std::sync::{Arc, Mutex};

/// Which monitor a gate update comes from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GateContributor {
    /// Active-VT switch monitor (`session::spawn_monitor`).
    Vt,
    /// Screen-lock / unlock signals from logind.
    Lock,
    /// Suspend/resume signals from logind (`PrepareForSleep`).
    Sleep,
}

impl GateContributor {
    fn index(self) -> usize {
        match self {
            GateContributor::Vt => 0,
            GateContributor::Lock => 1,
            GateContributor::Sleep => 2,
        }
    }

    fn name(self) -> &'static str {
        match self {
            GateContributor::Vt => "vt",
            GateContributor::Lock => "lock",
            GateContributor::Sleep => "sleep",
        }
    }
}

/// Merges per-contributor gate votes into the single hub gate.
pub struct GateCoordinator {
    /// One open/closed vote per contributor; `true` = open.
    states: Mutex<[bool; 3]>,
    hub: Arc<Hub>,
}

impl GateCoordinator {
    /// Creates a coordinator with every contributor voting "open".
    pub fn new(hub: Arc<Hub>) -> Self {
        Self {
            states: Mutex::new([true; 3]),
            hub,
        }
    }

    /// Updates one contributor's vote and re-drives the hub gate to the
    /// conjunction of all votes. Idempotent when the vote does not change.
    pub fn set(&self, contributor: GateContributor, open: bool) {
        let combined = {
            let mut states = self.states.lock().expect("gate states mutex poisoned");
            if states[contributor.index()] == open {
                return;
            }
            states[contributor.index()] = open;
            states.iter().all(|open| *open)
        };
        eprintln!(
            "battuta-inputd: gate contributor {} -> {} (combined: {})",
            contributor.name(),
            if open { "open" } else { "closed" },
            if combined { "open" } else { "closed" }
        );
        self.hub.set_gate(combined);
    }

    /// Current combined state (diagnostics and tests).
    pub fn is_open(&self) -> bool {
        self.states
            .lock()
            .expect("gate states mutex poisoned")
            .iter()
            .all(|open| *open)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hub::SourceEvent;
    use battuta_input_protocol as proto;
    use std::time::Duration;

    fn reset_reason(hub: &Hub) -> proto::ResetReason {
        match hub.recv_timeout(Duration::from_millis(5)).unwrap() {
            Some(SourceEvent::Reset { reason }) => reason,
            other => panic!("expected reset, got {other:?}"),
        }
    }

    #[test]
    fn all_contributors_must_be_open() {
        let hub = Arc::new(Hub::new(true));
        let coordinator = GateCoordinator::new(Arc::clone(&hub));
        assert!(coordinator.is_open());

        coordinator.set(GateContributor::Vt, false);
        assert!(!coordinator.is_open());
        assert_eq!(reset_reason(&hub), proto::ResetReason::SessionPause);

        // Closing a second gate must not emit another pause (hub gate did
        // not change).
        coordinator.set(GateContributor::Lock, false);
        assert!(!coordinator.is_open());
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);

        // Reopening one contributor is not enough while the other is closed.
        coordinator.set(GateContributor::Vt, true);
        assert!(!coordinator.is_open());
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);

        coordinator.set(GateContributor::Lock, true);
        assert!(coordinator.is_open());
        assert_eq!(reset_reason(&hub), proto::ResetReason::SessionResume);
    }

    #[test]
    fn contributor_transitions_are_idempotent() {
        let hub = Arc::new(Hub::new(true));
        let coordinator = GateCoordinator::new(Arc::clone(&hub));
        coordinator.set(GateContributor::Sleep, false);
        coordinator.set(GateContributor::Sleep, false);
        assert_eq!(reset_reason(&hub), proto::ResetReason::SessionPause);
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);
        coordinator.set(GateContributor::Sleep, true);
        coordinator.set(GateContributor::Sleep, true);
        assert_eq!(reset_reason(&hub), proto::ResetReason::SessionResume);
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);
    }

    #[test]
    fn events_are_dropped_while_any_contributor_is_closed() {
        let hub = Arc::new(Hub::new(true));
        let coordinator = GateCoordinator::new(Arc::clone(&hub));
        coordinator.set(GateContributor::Lock, false);
        let _pause = reset_reason(&hub);
        hub.push(SourceEvent::Key { code: 30, value: 1 });
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);
    }
}
