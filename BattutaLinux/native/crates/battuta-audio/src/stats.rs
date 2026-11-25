//! Realtime-observable counters. Every field is an atomic so the render
//! thread can bump counters without locks and any thread can read snapshots.

use std::sync::atomic::{AtomicI64, AtomicU32, Ordering};

#[derive(Default)]
pub struct AudioStats {
    /// Commands rejected because the ring was full or no voice was available
    /// without stealing permission (mirrors `DroppedCommandCount`).
    pub dropped_commands: AtomicI64,
    /// Active voices replaced by a steal (mirrors `VoiceStealCount`).
    pub voice_steals: AtomicI64,
    /// Render callbacks that finished with an empty output buffer because the
    /// PipeWire graph did not deliver data in time (underruns).
    pub underruns: AtomicI64,
    /// Stream rebuilds after device change, PipeWire restart, or errors.
    pub stream_recoveries: AtomicI64,
    /// Negotiated quantum in frames (0 until the stream is running).
    pub actual_quantum: AtomicU32,
}

impl AudioStats {
    pub fn dropped_commands(&self) -> i64 {
        self.dropped_commands.load(Ordering::Relaxed)
    }

    pub fn voice_steals(&self) -> i64 {
        self.voice_steals.load(Ordering::Relaxed)
    }

    pub fn underruns(&self) -> i64 {
        self.underruns.load(Ordering::Relaxed)
    }

    pub fn stream_recoveries(&self) -> i64 {
        self.stream_recoveries.load(Ordering::Relaxed)
    }

    pub fn actual_quantum(&self) -> u32 {
        self.actual_quantum.load(Ordering::Relaxed)
    }
}
