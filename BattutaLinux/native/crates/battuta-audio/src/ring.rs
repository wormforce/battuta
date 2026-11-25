//! Lock-free SPSC command ring carrying playback commands from the C#
//! caller (single producer, the FFI call on the input hot path) to the
//! realtime render thread (single consumer).
//!
//! Capacity is a fixed 512 entries (plan §6.1); a full ring rejects the
//! command and counts it as dropped, exactly like the Windows mixer's
//! queued-command cap. The ring itself never allocates.

use std::cell::UnsafeCell;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::mixer::StartVoiceCommand;

/// Ring capacity; power of two so index masking is a single AND.
pub const RING_CAPACITY: usize = crate::MAX_QUEUED_COMMANDS;

pub struct SpscCommandRing {
    /// Slot storage; written by the producer before the tail release, read
    /// by the consumer after the tail acquire.
    slots: [UnsafeCell<StartVoiceCommand>; RING_CAPACITY],
    /// Next slot index the producer will write. Never read by the consumer.
    head: AtomicUsize,
    /// Next slot index the consumer will read; lags or equals `head`.
    tail: AtomicUsize,
}

// One producer thread and one consumer thread touch disjoint cursors and
// each slot is only accessed by the side that currently owns it (the
// ownership handoff happens in the release/acquire of the cursor stores).
unsafe impl Send for SpscCommandRing {}
unsafe impl Sync for SpscCommandRing {}

impl SpscCommandRing {
    pub fn new() -> Self {
        // `StartVoiceCommand` is plain-old-data, so an empty array is valid.
        Self {
            slots: std::array::from_fn(|_| UnsafeCell::new(StartVoiceCommand::EMPTY)),
            head: AtomicUsize::new(0),
            tail: AtomicUsize::new(0),
        }
    }

    /// Queues a command if the ring has room. Returns `false` when full.
    pub fn push(&self, command: StartVoiceCommand) -> bool {
        let head = self.head.load(Ordering::Relaxed);
        let tail = self.tail.load(Ordering::Acquire);
        if head.wrapping_sub(tail) >= RING_CAPACITY {
            return false;
        }
        // SAFETY: `head - tail < capacity` proves the producer owns this slot;
        // the consumer only reads slots below `head`.
        unsafe {
            *self.slots[head & (RING_CAPACITY - 1)].get() = command;
        }
        self.head.store(head.wrapping_add(1), Ordering::Release);
        true
    }

    /// Pops the oldest command if any. Safe on the realtime thread: no
    /// allocation, no lock, one acquire load and one store.
    pub fn pop(&self) -> Option<StartVoiceCommand> {
        let tail = self.tail.load(Ordering::Relaxed);
        let head = self.head.load(Ordering::Acquire);
        if tail == head {
            return None;
        }
        // SAFETY: `tail != head` proves the consumer owns this slot; the
        // producer only writes slots at or above `head`. The slot is always
        // initialized (`EMPTY` at construction), so a plain copy-out is fine.
        let command = unsafe { std::ptr::read(self.slots[tail & (RING_CAPACITY - 1)].get()) };
        self.tail.store(tail.wrapping_add(1), Ordering::Release);
        Some(command)
    }

    /// Number of commands currently queued (snapshot for stats/telemetry).
    pub fn len(&self) -> usize {
        self.head
            .load(Ordering::Acquire)
            .wrapping_sub(self.tail.load(Ordering::Relaxed))
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl Default for SpscCommandRing {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mixer::StartVoiceCommand;

    #[test]
    fn push_pop_round_trip_preserves_order() {
        let ring = SpscCommandRing::new();
        for index in 0..64_u32 {
            assert!(ring.push(StartVoiceCommand {
                sample: index,
                gain: index as f32,
                rate: 1.0,
                allows_stealing: index % 2 == 0,
                generation: 0,
            }));
        }
        for index in 0..64_u32 {
            let command = ring.pop().expect("command");
            assert_eq!(command.sample, index);
            assert_eq!(command.allows_stealing, index % 2 == 0);
        }
        assert!(ring.pop().is_none());
        assert!(ring.is_empty());
    }

    #[test]
    fn full_ring_rejects_and_recovers() {
        let ring = SpscCommandRing::new();
        let mut pushed = 0;
        while ring.push(StartVoiceCommand::EMPTY) {
            pushed += 1;
        }
        assert_eq!(pushed, RING_CAPACITY);
        assert_eq!(ring.len(), RING_CAPACITY);
        assert!(ring.pop().is_some());
        assert!(ring.push(StartVoiceCommand::EMPTY));
    }
}
