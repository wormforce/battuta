//! Battuta native audio engine for Linux: a realtime-safe PipeWire output
//! shim behind a small C ABI (plan §6.1).
//!
//! Layering:
//!
//! - [`ring`] — the SPSC command ring (512 entries) that carries playback
//!   commands from the C# caller to the realtime render thread.
//! - [`bank`] — the sample registry: fixed slots, refcounted, retired slots
//!   are recycled only on the control thread (the realtime thread never
//!   allocates or frees).
//! - [`mixer`] — the sixteen-voice mixer, a line-faithful port of the Windows
//!   `PolyphonicSampleProvider` (round-robin allocation, oldest-voice steal,
//!   Catmull-Rom interpolation, post-mix clip) so both platforms render
//!   identically for the same command transcript (golden fixture).
//! - [`engine`] — ties ring + bank + mixer + stats together and renders
//!   frames; the PipeWire stream (feature `pipewire`) and the C ABI
//!   ([`ffi`]) are thin shells around it.
//!
//! Realtime rules (plan §6.1): the process callback never allocates, never
//! locks a mutex, never calls into .NET, never logs, and never touches the
//! file system. `PW_STREAM_FLAG_RT_PROCESS` is always set.

#![deny(unsafe_op_in_unsafe_fn)]

pub mod bank;
pub mod engine;
pub mod mixer;
pub mod ring;
pub mod stats;

#[cfg(feature = "pipewire")]
pub mod device_monitor;
#[cfg(feature = "pipewire")]
pub mod ffi;
#[cfg(feature = "pipewire")]
pub mod pipewire;

static PW_LOG_ENABLED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(true);

/// Engine warnings (control-thread only — the realtime callback never logs).
pub fn log_warn(message: &str) {
    if PW_LOG_ENABLED.load(std::sync::atomic::Ordering::Relaxed) {
        eprintln!("battuta-audio: {message}");
    }
}

/// Sample rate every prepared sample and every output stream uses (plan §6.1).
pub const SAMPLE_RATE: u32 = 48_000;
/// Output channel count: stereo float.
pub const OUTPUT_CHANNELS: usize = 2;
/// Fixed voice pool size.
pub const VOICE_COUNT: usize = 16;
/// Maximum commands queued between render callbacks.
pub const MAX_QUEUED_COMMANDS: usize = 512;
/// Idle frames (1 s at 48 kHz) tracked for the idle-deactivation study.
pub const IDLE_OUTPUT_FRAME_COUNT: u32 = SAMPLE_RATE;
/// Playback rate bounds, mirroring `AudioConstants`.
pub const MINIMUM_PLAYBACK_RATE: f32 = 0.25;
pub const MAXIMUM_PLAYBACK_RATE: f32 = 4.0;
