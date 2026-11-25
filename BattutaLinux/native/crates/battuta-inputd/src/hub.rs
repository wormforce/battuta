//! Event hub between device sources and the single active subscriber.
//!
//! Sources push neutral [`SourceEvent`]s; the per-connection pump drains them
//! and assigns per-session sequence numbers. The hub owns three contract
//! behaviors:
//!
//! - backlog: at most [`QUEUE_CAPACITY`] events are retained; overflow drops
//!   the oldest and arms a `Reset(BacklogDrop)` that is delivered before any
//!   retained event (contract §4, "keep newest").
//! - session gate: while the gate is closed (inactive VT) events are dropped
//!   at the source boundary and a `Reset(SessionPause)` / `Reset(SessionResume)`
//!   pair brackets the pause (contract reset reasons 3 and 4).
//! - shutdown: a closed hub makes every pump return, ending subscriber
//!   sessions cleanly.

use battuta_input_protocol as proto;
use std::collections::VecDeque;
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};

/// Maximum events retained for the active subscriber before the broker drops
/// the oldest and reports it via `Reset(BacklogDrop)`.
pub const QUEUE_CAPACITY: usize = 1024;

/// Neutral event flowing from sources to the subscriber pump. Sequence numbers
/// are assigned per-session by the pump, never by sources.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SourceEvent {
    Key { code: u16, value: u16 },
    Button { code: u16, value: u16 },
    Reset { reason: proto::ResetReason },
}

/// A closed hub: the pump must end the session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HubClosed;

struct HubState {
    queue: VecDeque<SourceEvent>,
    backlog_drop: bool,
    gate_open: bool,
    closed: bool,
}

/// Shared event hub. All operations are lock-based and non-async; event rates
/// are human scale so contention is irrelevant.
pub struct Hub {
    state: Mutex<HubState>,
    signal: Condvar,
}

impl Hub {
    /// Creates a hub with the given initial gate state. Brokers with a VT
    /// gate always start open here and take their first synchronous gate
    /// decision in server setup, which queues a `Reset(SessionPause)` for the
    /// first subscriber when the graphical VT is not active; gate-less
    /// brokers stay open forever.
    pub fn new(gate_open: bool) -> Self {
        Self {
            state: Mutex::new(HubState {
                queue: VecDeque::new(),
                backlog_drop: false,
                gate_open,
                closed: false,
            }),
            signal: Condvar::new(),
        }
    }

    /// Updates the session gate. `false` clears everything queued, drops all
    /// subsequent events until reopened, and queues `Reset(SessionPause)`;
    /// `true` queues `Reset(SessionResume)`. Both are idempotent no-ops when
    /// the state does not change.
    pub fn set_gate(&self, open: bool) {
        let mut state = self.state.lock().expect("hub mutex poisoned");
        if state.closed || state.gate_open == open {
            return;
        }
        state.gate_open = open;
        if open {
            state.queue.push_back(SourceEvent::Reset {
                reason: proto::ResetReason::SessionResume,
            });
        } else {
            state.queue.clear();
            state.backlog_drop = false;
            state.queue.push_back(SourceEvent::Reset {
                reason: proto::ResetReason::SessionPause,
            });
        }
        drop(state);
        self.signal.notify_all();
    }

    /// Enqueues one event. While the gate is closed events are dropped
    /// silently; the pause/resume resets keep client state honest (contract
    /// §4 forbids stuck modifiers across pauses).
    pub fn push(&self, event: SourceEvent) {
        let mut state = self.state.lock().expect("hub mutex poisoned");
        if state.closed || !state.gate_open {
            return;
        }
        if state.queue.len() >= QUEUE_CAPACITY {
            state.queue.pop_front();
            state.backlog_drop = true;
        }
        state.queue.push_back(event);
        drop(state);
        self.signal.notify_one();
    }

    /// Blocks up to `timeout` for the next deliverable event. `Ok(None)` means
    /// nothing arrived in the window. A backlog drop is delivered as a
    /// synthetic `Reset(BacklogDrop)` that precedes the retained events.
    pub fn recv_timeout(&self, timeout: Duration) -> Result<Option<SourceEvent>, HubClosed> {
        let deadline = Instant::now() + timeout;
        let mut state = self.state.lock().expect("hub mutex poisoned");
        loop {
            if state.backlog_drop {
                state.backlog_drop = false;
                return Ok(Some(SourceEvent::Reset {
                    reason: proto::ResetReason::BacklogDrop,
                }));
            }
            if let Some(event) = state.queue.pop_front() {
                return Ok(Some(event));
            }
            if state.closed {
                return Err(HubClosed);
            }
            let now = Instant::now();
            if now >= deadline {
                return Ok(None);
            }
            let (guard, _wait_result) = self
                .signal
                .wait_timeout(state, deadline - now)
                .expect("hub condvar poisoned");
            state = guard;
        }
    }

    /// Marks the hub closed; every waiting or future pump returns
    /// [`HubClosed`].
    pub fn close(&self) {
        let mut state = self.state.lock().expect("hub mutex poisoned");
        state.closed = true;
        drop(state);
        self.signal.notify_all();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reason_of(event: SourceEvent) -> proto::ResetReason {
        match event {
            SourceEvent::Reset { reason } => reason,
            other => panic!("expected reset, got {other:?}"),
        }
    }

    #[test]
    fn delivers_events_in_order() {
        let hub = Hub::new(true);
        hub.push(SourceEvent::Key { code: 30, value: 1 });
        hub.push(SourceEvent::Key { code: 30, value: 0 });
        assert_eq!(
            hub.recv_timeout(Duration::ZERO).unwrap(),
            Some(SourceEvent::Key { code: 30, value: 1 })
        );
        assert_eq!(
            hub.recv_timeout(Duration::ZERO).unwrap(),
            Some(SourceEvent::Key { code: 30, value: 0 })
        );
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);
    }

    #[test]
    fn backlog_overflow_drops_oldest_and_arms_reset() {
        let hub = Hub::new(true);
        for code in 0..QUEUE_CAPACITY as u16 {
            hub.push(SourceEvent::Key { code, value: 1 });
        }
        // One more event overflows: the oldest is dropped, reset is armed.
        hub.push(SourceEvent::Key {
            code: 9999,
            value: 1,
        });
        assert_eq!(
            reason_of(hub.recv_timeout(Duration::ZERO).unwrap().unwrap()),
            proto::ResetReason::BacklogDrop,
            "reset must precede retained events"
        );
        // The retained queue keeps the newest events (code 1..=9999).
        assert_eq!(
            hub.recv_timeout(Duration::ZERO).unwrap(),
            Some(SourceEvent::Key { code: 1, value: 1 })
        );
    }

    #[test]
    fn gate_close_pauses_and_brackets_with_resets() {
        let hub = Hub::new(true);
        hub.push(SourceEvent::Key { code: 30, value: 1 });
        hub.set_gate(false);
        // Queued events were cleared; a pause reset is queued.
        assert_eq!(
            reason_of(hub.recv_timeout(Duration::ZERO).unwrap().unwrap()),
            proto::ResetReason::SessionPause
        );
        // Events pushed while paused are dropped at the source boundary.
        hub.push(SourceEvent::Key { code: 31, value: 1 });
        assert_eq!(hub.recv_timeout(Duration::from_millis(5)).unwrap(), None);
        hub.set_gate(true);
        assert_eq!(
            reason_of(hub.recv_timeout(Duration::ZERO).unwrap().unwrap()),
            proto::ResetReason::SessionResume
        );
        // Gate transitions are idempotent.
        hub.set_gate(true);
        assert_eq!(hub.recv_timeout(Duration::ZERO).unwrap(), None);
    }

    #[test]
    fn closed_hub_reports_closed_after_draining() {
        let hub = Hub::new(true);
        hub.push(SourceEvent::Button {
            code: 0x110,
            value: 1,
        });
        hub.close();
        assert_eq!(
            hub.recv_timeout(Duration::ZERO).unwrap(),
            Some(SourceEvent::Button {
                code: 0x110,
                value: 1
            }),
            "queued events are still delivered before close"
        );
        assert_eq!(hub.recv_timeout(Duration::ZERO), Err(HubClosed));
        hub.push(SourceEvent::Key { code: 30, value: 1 });
        assert_eq!(hub.recv_timeout(Duration::ZERO), Err(HubClosed));
    }

    #[test]
    fn set_gate_after_close_is_harmless() {
        let hub = Hub::new(false);
        hub.close();
        hub.set_gate(true);
        assert_eq!(hub.recv_timeout(Duration::ZERO), Err(HubClosed));
    }
}
