//! Minimal stable C ABI (plan §6.1). The surface is intentionally small:
//! engine lifecycle, sample registry, scheduling, stream control, stats.
//! The C# wrapper (`Battuta.Engine.Audio.NativeAudioEngine`) mirrors it.
//!
//! Return conventions: `0` = success for control calls, `1`/`0` for boolean
//! operations (`schedule`), and a nonzero negative value for failures.

use std::ffi::{c_char, c_int, c_void};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use crate::OUTPUT_CHANNELS;
use crate::engine::AudioEngine;

/// `VoicePriority` values from the C# layer.
const PRIORITY_KEYBOARD: i32 = 0;

/// Opaque engine handle handed to C#.
pub struct Engine {
    audio: Arc<AudioEngine>,
    stream: Mutex<Option<crate::pipewire::PwStream>>,
    /// Extra stream properties (e.g. `remote.name`, `target.object`), set
    /// before the first stream start. Control-thread only.
    stream_overrides: Mutex<Vec<(String, String)>>,
    /// Watches the default output device and raises `EVENT_DEVICE_CHANGED`.
    /// Started with the stream; control-thread only. Declared last so it
    /// drops after the stream during engine destruction.
    monitor: Mutex<Option<crate::device_monitor::DeviceMonitor>>,
}

/// Flat stats snapshot for the C# marshaling.
#[repr(C)]
pub struct AudioStatsSnapshot {
    pub dropped_commands: i64,
    pub voice_steals: i64,
    pub underruns: i64,
    pub stream_recoveries: i64,
    pub actual_quantum: u32,
    pub rendered_frames: u64,
}

/// # Safety
///
/// `event_callback` must be a valid C-callee function or `None`; the engine
/// handle is written through `out_engine` on success and must later be
/// destroyed with [`battuta_audio_engine_destroy`] on the same thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_engine_create(
    event_callback: Option<unsafe extern "C" fn(i32, *mut c_void)>,
    event_user_data: *mut c_void,
    out_engine: *mut *mut Engine,
) -> c_int {
    if out_engine.is_null() {
        return -1;
    }
    let engine = Box::new(Engine {
        audio: AudioEngine::shared(),
        stream: Mutex::new(None),
        stream_overrides: Mutex::new(Vec::new()),
        monitor: Mutex::new(None),
    });
    engine
        .audio
        .set_event_callback(event_callback, event_user_data);
    // SAFETY: caller owns the returned pointer (frees via engine_destroy).
    unsafe { *out_engine = Box::into_raw(engine) };
    0
}

/// # Safety
/// `engine` must have come from `battuta_audio_engine_create` and must not
/// be used again afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_engine_destroy(engine: *mut Engine) {
    if engine.is_null() {
        return;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { Box::from_raw(engine) };
    if let Ok(mut stream) = engine.stream.lock() {
        *stream = None; // Drop the PipeWire stream while pw is initialized.
    }
    engine.audio.set_event_callback(None, std::ptr::null_mut());
}

/// Registers a mono 48 kHz float sample; returns the handle (0 = failure).
///
/// # Safety
/// `data` must point to `frame_count` readable f32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_sample_register(
    engine: *mut Engine,
    data: *const f32,
    frame_count: u32,
) -> u32 {
    if engine.is_null() || data.is_null() || frame_count == 0 {
        return 0;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    // SAFETY: caller contract above.
    let samples = unsafe { std::slice::from_raw_parts(data, frame_count as usize) }.to_vec();
    // Handle 0 is reserved as "no sample"; shift handles up by one.
    match engine.audio.bank.register(samples) {
        Some(handle) => handle + 1,
        None => 0,
    }
}

/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_sample_release(engine: *mut Engine, handle: u32) {
    if engine.is_null() || handle == 0 {
        return;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    engine.audio.bank.release(handle - 1);
}

/// Queues a playback command. Returns 1 when accepted, 0 when dropped
/// (ring full, non-finite values, or a pointer command with no free voice).
///
/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_schedule(
    engine: *mut Engine,
    sample: u32,
    gain: f32,
    rate: f32,
    priority: i32,
) -> c_int {
    if engine.is_null() || sample == 0 {
        return 0;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    let allows_stealing = priority == PRIORITY_KEYBOARD;
    let queued = engine.audio.mixer.try_schedule(
        &engine.audio.ring,
        &engine.audio.bank,
        &engine.audio.stats,
        sample - 1,
        gain,
        rate,
        allows_stealing,
    );
    c_int::from(queued)
}

/// Invalidates queued commands and clears active voices (output restart).
///
/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_reset(engine: *mut Engine) {
    if engine.is_null() {
        return;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    engine.audio.mixer.reset_for_output_restart();
}

/// Starts the PipeWire output stream. Returns 0 on success (asynchronous:
/// the `EVENT_STREAM_READY` event reports actual readiness).
///
/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_stream_start(engine: *mut Engine) -> c_int {
    if engine.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &mut *engine };
    let mut stream_slot = match engine.stream.lock() {
        Ok(slot) => slot,
        Err(_) => return -2,
    };
    if stream_slot.is_some() {
        return 0; // Already running.
    }
    let overrides = engine
        .stream_overrides
        .lock()
        .map(|slots| slots.clone())
        .unwrap_or_default();
    match crate::pipewire::PwStream::start(Arc::clone(&engine.audio), &overrides) {
        Ok(stream) => {
            *stream_slot = Some(stream);
            // The device monitor rides along with the stream: it needs the
            // daemon anyway, and without a stream the host has nothing to
            // rebuild. Failure only means no device-change events.
            let mut monitor_slot = match engine.monitor.lock() {
                Ok(slot) => slot,
                Err(_) => return 0,
            };
            if monitor_slot.is_none() {
                match crate::device_monitor::DeviceMonitor::start(Arc::clone(&engine.audio)) {
                    Ok(monitor) => *monitor_slot = Some(monitor),
                    Err(error) => {
                        crate::log_warn(&format!("device monitor unavailable: {error}"));
                    }
                }
            }
            0
        }
        Err(error) => {
            crate::log_warn(&format!("stream start failed: {error}"));
            -3
        }
    }
}

/// Stops the PipeWire output stream (the engine and its samples survive).
///
/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_stream_stop(engine: *mut Engine) -> c_int {
    if engine.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &mut *engine };
    let mut stream_slot = match engine.stream.lock() {
        Ok(slot) => slot,
        Err(_) => return -2,
    };
    engine.audio.mixer.reset_for_output_restart();
    *stream_slot = None;
    0
}

/// Tears down and recreates the stream (device change, PipeWire restart,
/// sleep resume). Counts a recovery on success.
///
/// # Safety
/// `engine` must be a live engine handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_stream_rebuild(engine: *mut Engine) -> c_int {
    if engine.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &mut *engine };
    let mut stream_slot = match engine.stream.lock() {
        Ok(slot) => slot,
        Err(_) => return -2,
    };
    engine.audio.mixer.reset_for_output_restart();
    let overrides = engine
        .stream_overrides
        .lock()
        .map(|slots| slots.clone())
        .unwrap_or_default();
    match stream_slot.as_mut() {
        Some(stream) => match stream.rebuild(&overrides) {
            Ok(()) => {
                engine
                    .audio
                    .stats
                    .stream_recoveries
                    .fetch_add(1, Ordering::Relaxed);
                0
            }
            Err(error) => {
                crate::log_warn(&format!("stream rebuild failed: {error}"));
                *stream_slot = None;
                -3
            }
        },
        None => {
            // Nothing to rebuild: start a fresh stream.
            match crate::pipewire::PwStream::start(Arc::clone(&engine.audio), &overrides) {
                Ok(stream) => {
                    *stream_slot = Some(stream);
                    engine
                        .audio
                        .stats
                        .stream_recoveries
                        .fetch_add(1, Ordering::Relaxed);
                    0
                }
                Err(error) => {
                    crate::log_warn(&format!("stream start failed during rebuild: {error}"));
                    -3
                }
            }
        }
    }
}

/// Sets an extra PipeWire stream property (e.g. `remote.name`,
/// `target.object`). Must be called before the first stream start.
///
/// # Safety
/// `engine` must be a live engine handle; `key`/`value` valid C strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_set_stream_property(
    engine: *mut Engine,
    key: *const c_char,
    value: *const c_char,
) -> c_int {
    if engine.is_null() || key.is_null() || value.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    let mut overrides = match engine.stream_overrides.lock() {
        Ok(overrides) => overrides,
        Err(_) => return -2,
    };
    // SAFETY: caller contract above.
    let (key, value) = unsafe {
        (
            std::ffi::CStr::from_ptr(key),
            std::ffi::CStr::from_ptr(value),
        )
    };
    match (key.to_str(), value.to_str()) {
        (Ok(key), Ok(value)) => {
            overrides.push((key.to_owned(), value.to_owned()));
            0
        }
        _ => -3,
    }
}

/// Snapshots the engine stats.
///
/// # Safety
/// `engine` must be a live engine handle; `out` a writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_get_stats(
    engine: *mut Engine,
    out_stats: *mut AudioStatsSnapshot,
) -> c_int {
    if engine.is_null() || out_stats.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    let engine = unsafe { &*engine };
    let audio = &engine.audio;
    let snapshot = AudioStatsSnapshot {
        dropped_commands: audio.stats.dropped_commands(),
        voice_steals: audio.stats.voice_steals(),
        underruns: audio.stats.underruns(),
        stream_recoveries: audio.stats.stream_recoveries(),
        actual_quantum: audio.stats.actual_quantum(),
        rendered_frames: audio.rendered_frames.load(Ordering::Relaxed),
    };
    // SAFETY: caller contract above.
    unsafe { *out_stats = snapshot };
    0
}

/// Protocol/format summary for the C# side to assert compatibility.
///
/// # Safety
/// `engine` may be null; `out` a writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn battuta_audio_get_format(
    out_sample_rate: *mut u32,
    out_channels: *mut u32,
) -> c_int {
    if out_sample_rate.is_null() || out_channels.is_null() {
        return -1;
    }
    // SAFETY: caller contract above.
    unsafe {
        *out_sample_rate = crate::SAMPLE_RATE;
        *out_channels = OUTPUT_CHANNELS as u32;
    }
    0
}
