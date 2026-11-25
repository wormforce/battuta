//! Hand-rolled PipeWire FFI plus the realtime output stream (plan §6.1).
//!
//! Only the API surface Battuta needs is declared — matching the project's
//! hand-written-protocol style (no `-sys` crates). The realtime process
//! callback dequeues a buffer, renders through the engine mixer, and queues
//! it back; it never allocates, locks, logs, or calls into .NET.

use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};

use crate::OUTPUT_CHANNELS;
use crate::engine::AudioEngine;

// ---------------------------------------------------------------------------
// Constants (extracted from the installed headers; see docs/devlog PR 6).
// ---------------------------------------------------------------------------

const PW_ID_ANY: u32 = u32::MAX;
const PW_VERSION_STREAM_EVENTS: u32 = 2;
const PW_STREAM_FLAG_AUTOCONNECT: u32 = 1 << 0;
const PW_STREAM_FLAG_MAP_BUFFERS: u32 = 1 << 2;
const PW_STREAM_FLAG_RT_PROCESS: u32 = 1 << 4;
const PW_DIRECTION_OUTPUT: c_int = 1;

const PW_STREAM_STATE_ERROR: c_int = -1;
const PW_STREAM_STATE_STREAMING: c_int = 3;

const SPA_TYPE_OBJECT: u32 = 15;
const SPA_TYPE_INT: u32 = 4;
const SPA_TYPE_ID: u32 = 3;
const SPA_TYPE_ARRAY: u32 = 13;
const SPA_TYPE_OBJECT_FORMAT: u32 = 0x4_0003; // 262147
const SPA_PARAM_ENUM_FORMAT: u32 = 3;
const SPA_FORMAT_MEDIA_TYPE: u32 = 1;
const SPA_FORMAT_MEDIA_SUBTYPE: u32 = 2;
const SPA_FORMAT_AUDIO_FORMAT: u32 = 0x1_0001;
const SPA_FORMAT_AUDIO_RATE: u32 = 0x1_0003;
const SPA_FORMAT_AUDIO_CHANNELS: u32 = 0x1_0004;
const SPA_FORMAT_AUDIO_POSITION: u32 = 0x1_0005;
const SPA_MEDIA_TYPE_AUDIO: u32 = 1;
const SPA_MEDIA_SUBTYPE_RAW: u32 = 1;
const SPA_AUDIO_FORMAT_F32_LE: u32 = 283;
const SPA_AUDIO_CHANNEL_FL: u32 = 3;
const SPA_AUDIO_CHANNEL_FR: u32 = 4;

// ---------------------------------------------------------------------------
// Raw FFI declarations.
// ---------------------------------------------------------------------------

#[repr(C)]
struct SpaBuffer {
    n_metas: u32,
    n_datas: u32,
    metas: *mut c_void,
    datas: *mut SpaData,
}

#[repr(C)]
struct SpaData {
    type_: u32,
    flags: u32,
    fd: i64,
    mapoffset: u32,
    maxsize: u32,
    data: *mut c_void,
    chunk: *mut SpaChunk,
}

#[repr(C)]
struct SpaChunk {
    offset: u32,
    size: u32,
    stride: i32,
    flags: i32,
}

#[repr(C)]
struct PwBuffer {
    buffer: *mut SpaBuffer,
    user_data: *mut c_void,
    size: u64,
    requested: u64,
}

#[repr(C)]
struct PwStreamEvents {
    version: u32,
    destroy: Option<unsafe extern "C" fn(*mut c_void)>,
    state_changed: Option<unsafe extern "C" fn(*mut c_void, c_int, c_int, *const c_char)>,
    control_info: Option<unsafe extern "C" fn()>,
    io_changed: Option<unsafe extern "C" fn()>,
    param_changed: Option<unsafe extern "C" fn()>,
    add_buffer: Option<unsafe extern "C" fn()>,
    remove_buffer: Option<unsafe extern "C" fn()>,
    process: Option<unsafe extern "C" fn(*mut c_void)>,
    drained: Option<unsafe extern "C" fn()>,
    command: Option<unsafe extern "C" fn()>,
    trigger_done: Option<unsafe extern "C" fn()>,
}

#[link(name = "pipewire-0.3")]
unsafe extern "C" {
    fn pw_init(argc: *mut c_int, argv: *mut *mut *mut c_char);
    #[link_name = "pw_thread_loop_new"]
    fn pw_thread_loop_new(name: *const c_char, props: *const c_void) -> *mut c_void;
    #[link_name = "pw_thread_loop_destroy"]
    fn pw_thread_loop_destroy(loop_: *mut c_void);
    #[link_name = "pw_thread_loop_start"]
    fn pw_thread_loop_start(loop_: *mut c_void) -> c_int;
    #[link_name = "pw_thread_loop_stop"]
    fn pw_thread_loop_stop(loop_: *mut c_void);
    #[link_name = "pw_thread_loop_lock"]
    fn pw_thread_loop_lock(loop_: *mut c_void);
    #[link_name = "pw_thread_loop_unlock"]
    fn pw_thread_loop_unlock(loop_: *mut c_void);
    #[link_name = "pw_thread_loop_get_loop"]
    fn pw_thread_loop_get_loop(loop_: *mut c_void) -> *mut c_void;
    #[link_name = "pw_properties_new_string"]
    fn pw_properties_new_string(args: *const c_char) -> *mut c_void;
    #[link_name = "pw_stream_new_simple"]
    fn pw_stream_new_simple(
        loop_: *mut c_void,
        name: *const c_char,
        properties: *mut c_void,
        events: *const PwStreamEvents,
        data: *mut c_void,
    ) -> *mut c_void;
    #[link_name = "pw_stream_destroy"]
    fn pw_stream_destroy(stream: *mut c_void);
    #[link_name = "pw_stream_connect"]
    fn pw_stream_connect(
        stream: *mut c_void,
        direction: c_int,
        target_id: u32,
        flags: u32,
        params: *const *const c_void,
        n_params: c_int,
    ) -> c_int;
    #[link_name = "pw_stream_dequeue_buffer"]
    fn pw_stream_dequeue_buffer(stream: *mut c_void) -> *mut PwBuffer;
    #[link_name = "pw_stream_queue_buffer"]
    fn pw_stream_queue_buffer(stream: *mut c_void, buffer: *mut PwBuffer) -> c_int;
}

// ---------------------------------------------------------------------------
// Format POD (hand-encoded; byte-identical to spa_format_audio_raw_build's
// output for f32 LE / 48 kHz / stereo FL+FR — verified against the C builder).
// ---------------------------------------------------------------------------

fn push_pod_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&4_u32.to_le_bytes()); // size
    out.extend_from_slice(&SPA_TYPE_ID.to_le_bytes()); // type
    out.extend_from_slice(&value.to_le_bytes());
    out.extend_from_slice(&[0; 4]); // 8-byte alignment padding
}

fn push_pod_int(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&4_u32.to_le_bytes());
    out.extend_from_slice(&SPA_TYPE_INT.to_le_bytes());
    out.extend_from_slice(&value.to_le_bytes());
    out.extend_from_slice(&[0; 4]);
}

fn push_pod_prop(out: &mut Vec<u8>, key: u32, value: &mut Vec<u8>) {
    out.extend_from_slice(&key.to_le_bytes());
    out.extend_from_slice(&0_u32.to_le_bytes()); // flags
    out.append(value);
}

/// Builds the `SPA_PARAM_EnumFormat` pod: audio/raw f32 LE, 48 kHz, stereo.
fn build_format_pod() -> Vec<u8> {
    let mut out = Vec::with_capacity(168);
    // Object header; the 160-byte size is patched in at the end.
    let size_offset = out.len();
    out.extend_from_slice(&0_u32.to_le_bytes());
    out.extend_from_slice(&SPA_TYPE_OBJECT.to_le_bytes());
    out.extend_from_slice(&SPA_TYPE_OBJECT_FORMAT.to_le_bytes());
    out.extend_from_slice(&SPA_PARAM_ENUM_FORMAT.to_le_bytes());

    let mut value = Vec::new();
    push_pod_u32(&mut value, SPA_MEDIA_TYPE_AUDIO);
    push_pod_prop(&mut out, SPA_FORMAT_MEDIA_TYPE, &mut value);

    let mut value = Vec::new();
    push_pod_u32(&mut value, SPA_MEDIA_SUBTYPE_RAW);
    push_pod_prop(&mut out, SPA_FORMAT_MEDIA_SUBTYPE, &mut value);

    let mut value = Vec::new();
    push_pod_u32(&mut value, SPA_AUDIO_FORMAT_F32_LE);
    push_pod_prop(&mut out, SPA_FORMAT_AUDIO_FORMAT, &mut value);

    let mut value = Vec::new();
    push_pod_int(&mut value, crate::SAMPLE_RATE);
    push_pod_prop(&mut out, SPA_FORMAT_AUDIO_RATE, &mut value);

    let mut value = Vec::new();
    push_pod_int(&mut value, OUTPUT_CHANNELS as u32);
    push_pod_prop(&mut out, SPA_FORMAT_AUDIO_CHANNELS, &mut value);

    // Channel position array: pod {16, Array} + child pod {4, Id} + FL, FR.
    // The child pod header (8 bytes) plus 2 ids make up the 16-byte payload;
    // no trailing padding because it is the last property.
    let mut array = Vec::new();
    array.extend_from_slice(&16_u32.to_le_bytes());
    array.extend_from_slice(&SPA_TYPE_ARRAY.to_le_bytes());
    array.extend_from_slice(&4_u32.to_le_bytes());
    array.extend_from_slice(&SPA_TYPE_ID.to_le_bytes());
    array.extend_from_slice(&SPA_AUDIO_CHANNEL_FL.to_le_bytes());
    array.extend_from_slice(&SPA_AUDIO_CHANNEL_FR.to_le_bytes());
    push_pod_prop(&mut out, SPA_FORMAT_AUDIO_POSITION, &mut array);

    let payload_len = (out.len() - size_offset - 8) as u32;
    out[size_offset..size_offset + 4].copy_from_slice(&payload_len.to_le_bytes());
    out
}

// ---------------------------------------------------------------------------
// Stream state and callbacks.
// ---------------------------------------------------------------------------

/// Events delivered to the C# layer. Not the realtime thread — these fire
/// from the PipeWire main loop.
pub const EVENT_STREAM_READY: i32 = 1;
pub const EVENT_STREAM_ERROR: i32 = 2;

pub(crate) struct StreamUserData {
    pub engine: Arc<AudioEngine>,
    /// Set once the stream exists; read by the RT process callback.
    pub stream: AtomicU64,
    /// Frames of the last rendered quantum (the negotiated quantum).
    pub observed_quantum: AtomicU32,
    pub streaming: AtomicBool,
}

unsafe extern "C" fn on_process(data: *mut c_void) {
    // SAFETY: `data` points at the stream's user data (owned by `PwStream`)
    // for the lifetime of the stream; the buffer chain below is guaranteed
    // valid by PipeWire between dequeue and queue.
    unsafe {
        let user = &*(data as *mut StreamUserData);
        let stream = user.stream.load(Ordering::Acquire) as *mut c_void;
        if stream.is_null() {
            return;
        }
        let buffer = pw_stream_dequeue_buffer(stream);
        if buffer.is_null() {
            // No buffer available: PipeWire counts this as an underrun on our
            // side; the mixer state is untouched and the next cycle retries.
            user.engine.stats.underruns.fetch_add(1, Ordering::Relaxed);
            return;
        }

        let spa_buffer = (*buffer).buffer;
        let data = &*(*spa_buffer).datas;
        let bytes_per_frame = (OUTPUT_CHANNELS * std::mem::size_of::<f32>()) as u32;
        let frame_capacity = data.maxsize / bytes_per_frame;
        let requested = ((*buffer).requested as u32).min(frame_capacity);
        let frames = if requested > 0 {
            requested
        } else {
            frame_capacity
        };

        let output = std::slice::from_raw_parts_mut(
            data.data as *mut f32,
            frames as usize * OUTPUT_CHANNELS,
        );
        // SAFETY: the pw process callback is the single realtime render thread.
        let idle = user.engine.mixer.render(
            &user.engine.ring,
            &user.engine.bank,
            &user.engine.stats,
            output,
        );
        if !idle {
            user.engine
                .rendered_frames
                .fetch_add(frames as u64, Ordering::Relaxed);
        }

        let chunk = &mut *data.chunk;
        chunk.offset = 0;
        chunk.size = frames * bytes_per_frame;
        chunk.stride = (OUTPUT_CHANNELS * std::mem::size_of::<f32>()) as i32;
        (*buffer).size = frames as u64;
        user.observed_quantum.store(frames, Ordering::Relaxed);
        user.engine
            .stats
            .actual_quantum
            .store(frames, Ordering::Relaxed);
        pw_stream_queue_buffer(stream, buffer);
    }
}

unsafe extern "C" fn on_state_changed(
    data: *mut c_void,
    _old: c_int,
    state: c_int,
    error: *const c_char,
) {
    // SAFETY: `data` points at the stream's user data (owned by `PwStream`);
    // `error` is a valid C string or null per the PipeWire contract.
    unsafe {
        let user = &*(data as *mut StreamUserData);
        crate::log_warn(&format!("stream state changed: {state}"));
        if state == PW_STREAM_STATE_STREAMING {
            user.streaming.store(true, Ordering::Release);
            if let Some(event) = user.engine.event_callback() {
                event(EVENT_STREAM_READY, user.engine.event_user_data());
            }
        } else if state == PW_STREAM_STATE_ERROR {
            user.streaming.store(false, Ordering::Release);
            let message = if error.is_null() {
                String::new()
            } else {
                CStr::from_ptr(error).to_string_lossy().into_owned()
            };
            crate::log_warn(&format!("PipeWire stream error: {message}"));
            if let Some(event) = user.engine.event_callback() {
                event(EVENT_STREAM_ERROR, user.engine.event_user_data());
            }
        } else {
            user.streaming.store(false, Ordering::Release);
        }
    }
}

// ---------------------------------------------------------------------------
// Stream wrapper.
// ---------------------------------------------------------------------------

pub struct PwStream {
    thread_loop: *mut c_void,
    stream: *mut c_void,
    user_data: Box<StreamUserData>,
    events: Box<PwStreamEvents>,
}

// All PipeWire objects live in C and the thread loop serializes control
// access; the RT callback only touches the atomics in `user_data`.
unsafe impl Send for PwStream {}
unsafe impl Sync for PwStream {}

impl PwStream {
    /// Creates the PipeWire thread loop and stream and connects them to the
    /// default output. `overrides` are extra stream properties (tests use
    /// `remote.name` / `target.object`).
    pub fn start(engine: Arc<AudioEngine>, overrides: &[(String, String)]) -> Result<Self, String> {
        pipewire_init();
        let name = CString::new("battuta-audio-loop").unwrap();
        // SAFETY: no properties; PipeWire allocates its own state.
        let thread_loop = unsafe { pw_thread_loop_new(name.as_ptr(), std::ptr::null()) };
        if thread_loop.is_null() {
            return Err("pw_thread_loop_new failed".to_owned());
        }
        // The stream is created and connected while the loop is still
        // stopped (the canonical PipeWire pattern); the loop starts last.

        let user_data = Box::new(StreamUserData {
            engine,
            stream: AtomicU64::new(0),
            observed_quantum: AtomicU32::new(0),
            streaming: AtomicBool::new(false),
        });
        let events = Box::new(PwStreamEvents {
            version: PW_VERSION_STREAM_EVENTS,
            destroy: None,
            state_changed: Some(on_state_changed),
            control_info: None,
            io_changed: None,
            param_changed: None,
            add_buffer: None,
            remove_buffer: None,
            process: Some(on_process),
            drained: None,
            command: None,
            trigger_done: None,
        });

        let properties = Self::build_properties(overrides)?;
        let loop_ = unsafe { pw_thread_loop_get_loop(thread_loop) };
        let stream_name = CString::new("Battuta output").unwrap();
        // SAFETY: all pointers outlive the stream (stored in Self); the
        // events box and user data box are owned by the returned struct.
        // The loop is not running yet, so no lock is held.
        let stream = unsafe {
            let stream = pw_stream_new_simple(
                loop_,
                stream_name.as_ptr(),
                properties,
                &*events,
                &*user_data as *const StreamUserData as *mut c_void,
            );
            if stream.is_null() {
                pw_thread_loop_destroy(thread_loop);
                return Err("pw_stream_new_simple failed".to_owned());
            }
            let format_pod = build_format_pod();
            let params = [format_pod.as_ptr() as *const c_void];
            let connected = pw_stream_connect(
                stream,
                PW_DIRECTION_OUTPUT,
                PW_ID_ANY,
                PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS | PW_STREAM_FLAG_RT_PROCESS,
                params.as_ptr(),
                params.len() as c_int,
            );
            if connected != 0 {
                pw_stream_destroy(stream);
                pw_thread_loop_destroy(thread_loop);
                return Err(format!("pw_stream_connect failed: {connected}"));
            }
            stream
        };
        user_data.stream.store(stream as u64, Ordering::Release);

        // SAFETY: stream and callbacks are fully wired; the loop drives the
        // connected stream from here on.
        let started = unsafe { pw_thread_loop_start(thread_loop) };
        if started != 0 {
            user_data.stream.store(0, Ordering::Release);
            unsafe {
                pw_stream_destroy(stream);
                pw_thread_loop_destroy(thread_loop);
            }
            return Err(format!("pw_thread_loop_start failed: {started}"));
        }

        Ok(Self {
            thread_loop,
            stream,
            user_data,
            events,
        })
    }

    fn build_properties(overrides: &[(String, String)]) -> Result<*mut c_void, String> {
        // node.latency requests the plan's 128/48000 quantum; the actual
        // quantum is recorded from the process callback (the graph may
        // settle on a different, larger value). Host-provided overrides
        // (remote.name, target.object for tests) are appended last.
        let mut spec = String::from(
            "media.type=Audio media.category=Playback media.role=Product \
             application.name=Battuta node.name=battuta-audio \
             node.latency=128/48000 stream.monitor.silent=true",
        );
        for (key, value) in overrides {
            spec.push_str(&format!(" {key}={value}"));
        }
        let spec = CString::new(spec).map_err(|_| "invalid property string")?;
        // SAFETY: spec outlives the call.
        let properties = unsafe { pw_properties_new_string(spec.as_ptr()) };
        if properties.is_null() {
            return Err("pw_properties_new_string failed".to_owned());
        }
        Ok(properties)
    }

    /// Waits (bounded) for the stream to reach the STREAMING state.
    pub fn wait_streaming(&self, timeout: std::time::Duration) -> bool {
        let deadline = std::time::Instant::now() + timeout;
        while std::time::Instant::now() < deadline {
            if self.user_data.streaming.load(Ordering::Acquire) {
                return true;
            }
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        false
    }

    pub fn observed_quantum(&self) -> u32 {
        self.user_data.observed_quantum.load(Ordering::Relaxed)
    }

    /// Drops and recreates the stream on the same thread loop (recovery).
    pub fn rebuild(&mut self, overrides: &[(String, String)]) -> Result<(), String> {
        // The loop is running at this point; all stream surgery holds the
        // thread loop lock.
        unsafe { pw_thread_loop_lock(self.thread_loop) };
        self.teardown_stream();
        let properties = Self::build_properties(overrides);
        // SAFETY: the thread loop lock is held (see above).
        let stream = unsafe { self.build_stream_locked(properties) };
        unsafe { pw_thread_loop_unlock(self.thread_loop) };
        let stream = stream?;
        self.stream = stream;
        self.user_data
            .stream
            .store(self.stream as u64, Ordering::Release);
        Ok(())
    }

    /// Creates and connects a fresh stream. Callers hold the thread loop
    /// lock (or the loop is not running yet).
    ///
    /// # Safety
    /// See the FFI declarations; all owned pointers must outlive the stream.
    unsafe fn build_stream_locked(
        &self,
        properties: Result<*mut c_void, String>,
    ) -> Result<*mut c_void, String> {
        let properties = properties?;
        let loop_ = unsafe { pw_thread_loop_get_loop(self.thread_loop) };
        let stream_name = CString::new("Battuta output").unwrap();
        let stream = unsafe {
            let stream = pw_stream_new_simple(
                loop_,
                stream_name.as_ptr(),
                properties,
                &*self.events,
                &*self.user_data as *const StreamUserData as *mut c_void,
            );
            if stream.is_null() {
                return Err("pw_stream_new_simple failed during rebuild".to_owned());
            }
            let format_pod = build_format_pod();
            let params = [format_pod.as_ptr() as *const c_void];
            let connected = pw_stream_connect(
                stream,
                PW_DIRECTION_OUTPUT,
                PW_ID_ANY,
                PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS | PW_STREAM_FLAG_RT_PROCESS,
                params.as_ptr(),
                params.len() as c_int,
            );
            if connected != 0 {
                pw_stream_destroy(stream);
                return Err(format!(
                    "pw_stream_connect failed during rebuild: {connected}"
                ));
            }
            stream
        };
        Ok(stream)
    }

    /// Drops the current stream. Callers hold the thread loop lock (or the
    /// loop is not running).
    fn teardown_stream(&mut self) {
        if self.user_data.stream.swap(0, Ordering::AcqRel) != 0 {
            // SAFETY: stream handle is valid (owned) and detached from the
            // user data, so no callback can use it concurrently.
            unsafe { pw_stream_destroy(self.stream) };
            self.stream = std::ptr::null_mut();
        }
    }
}

impl Drop for PwStream {
    fn drop(&mut self) {
        unsafe { pw_thread_loop_lock(self.thread_loop) };
        self.teardown_stream();
        unsafe { pw_thread_loop_unlock(self.thread_loop) };
        // SAFETY: loop is valid; stop then destroy.
        unsafe {
            pw_thread_loop_stop(self.thread_loop);
            pw_thread_loop_destroy(self.thread_loop);
        }
    }
}

static PW_INITIALIZED: AtomicBool = AtomicBool::new(false);

/// `pw_init` must run exactly once per process before any PipeWire call.
pub(crate) fn pipewire_init() {
    if !PW_INITIALIZED.swap(true, Ordering::AcqRel) {
        // SAFETY: pw_init with no args.
        unsafe { pw_init(std::ptr::null_mut(), std::ptr::null_mut()) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The pod must be byte-identical to what `spa_format_audio_raw_build`
    /// emits (reference dump captured in the PR 6 devlog).
    #[test]
    fn format_pod_matches_the_c_builder() {
        let pod = build_format_pod();
        assert_eq!(pod.len(), 168);
        assert_eq!(&pod[..4], &160_u32.to_le_bytes(), "object size");
        assert_eq!(&pod[4..8], &SPA_TYPE_OBJECT.to_le_bytes());
        assert_eq!(&pod[8..12], &SPA_TYPE_OBJECT_FORMAT.to_le_bytes());
        assert_eq!(&pod[12..16], &SPA_PARAM_ENUM_FORMAT.to_le_bytes());
        // mediaType prop: key 1, flags 0, pod {4, Id}, value 1, pad 4.
        assert_eq!(&pod[16..20], &1_u32.to_le_bytes());
        assert_eq!(&pod[24..28], &4_u32.to_le_bytes());
        assert_eq!(&pod[28..32], &SPA_TYPE_ID.to_le_bytes());
        assert_eq!(&pod[32..36], &1_u32.to_le_bytes());
        // position array pod at the end: {16, Array} + {4, Id} + FL + FR.
        let tail = &pod[pod.len() - 24..];
        assert_eq!(&tail[..4], &16_u32.to_le_bytes());
        assert_eq!(&tail[4..8], &SPA_TYPE_ARRAY.to_le_bytes());
        assert_eq!(&tail[8..12], &4_u32.to_le_bytes());
        assert_eq!(&tail[12..16], &SPA_TYPE_ID.to_le_bytes());
        assert_eq!(&tail[16..20], &SPA_AUDIO_CHANNEL_FL.to_le_bytes());
        assert_eq!(&tail[20..24], &SPA_AUDIO_CHANNEL_FR.to_le_bytes());
    }
}
