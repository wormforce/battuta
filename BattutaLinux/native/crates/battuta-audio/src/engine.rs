//! The audio engine: ring + bank + mixer + stats under one object. The
//! PipeWire process callback ([`crate::pipewire`]) and the C ABI
//! ([`crate::ffi`]) are thin shells around this type.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::bank::SampleBank;
use crate::mixer::Mixer;
use crate::ring::SpscCommandRing;
use crate::stats::AudioStats;

pub struct AudioEngine {
    pub ring: SpscCommandRing,
    pub bank: SampleBank,
    pub mixer: Mixer,
    pub stats: AudioStats,
    /// Rendered-frame counter for diagnostics; the RT thread bumps it and
    /// readers snapshot it.
    pub rendered_frames: AtomicU64,
    /// Stream event callback registered by the host (C# layer). Read from
    /// the PipeWire main-loop thread; never from the RT callback.
    event_callback: AtomicU64,
    event_user_data: AtomicU64,
}

// The callback and user-data pointers are plain function/data pointers
// managed by the host; they are only invoked from the PipeWire main loop.
unsafe impl Send for AudioEngine {}
unsafe impl Sync for AudioEngine {}

impl AudioEngine {
    pub fn new() -> Self {
        Self {
            ring: SpscCommandRing::new(),
            bank: SampleBank::new(),
            mixer: Mixer::new(),
            stats: AudioStats::default(),
            rendered_frames: AtomicU64::new(0),
            event_callback: AtomicU64::new(0),
            event_user_data: AtomicU64::new(0),
        }
    }

    /// Registers the host event callback (`EVENT_STREAM_READY` /
    /// `EVENT_STREAM_ERROR`). Passing `None` unregisters.
    pub fn set_event_callback(
        &self,
        callback: Option<unsafe extern "C" fn(i32, *mut std::ffi::c_void)>,
        user_data: *mut std::ffi::c_void,
    ) {
        let function = match callback {
            Some(function) => function as usize as u64,
            None => 0,
        };
        self.event_callback.store(function, Ordering::Release);
        self.event_user_data
            .store(user_data as usize as u64, Ordering::Release);
    }

    pub(crate) fn event_callback(
        &self,
    ) -> Option<unsafe extern "C" fn(i32, *mut std::ffi::c_void)> {
        let function = self.event_callback.load(Ordering::Acquire);
        if function == 0 {
            None
        } else {
            // SAFETY: the host registered a valid function pointer.
            Some(unsafe {
                std::mem::transmute::<u64, unsafe extern "C" fn(i32, *mut std::ffi::c_void)>(
                    function,
                )
            })
        }
    }

    pub(crate) fn event_user_data(&self) -> *mut std::ffi::c_void {
        self.event_user_data.load(Ordering::Acquire) as usize as *mut std::ffi::c_void
    }

    /// Convenience constructor for hosts that start from a shared engine
    /// ([`Arc`]) — used by the C ABI engine wrapper.
    pub fn shared() -> Arc<AudioEngine> {
        Arc::new(AudioEngine::new())
    }
}

impl Default for AudioEngine {
    fn default() -> Self {
        Self::new()
    }
}
