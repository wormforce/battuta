//! Manual smoke test against an already-running PipeWire daemon:
//! `XDG_RUNTIME_DIR=/tmp/pwtest/runtime cargo run -p battuta-audio --example smoke`

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

static STREAM_READY: AtomicBool = AtomicBool::new(false);
static STREAM_ERROR: AtomicBool = AtomicBool::new(false);

unsafe extern "C" fn on_event(event: i32, _user_data: *mut std::ffi::c_void) {
    if event == 1 {
        STREAM_READY.store(true, Ordering::Release);
    } else if event == 2 {
        STREAM_ERROR.store(true, Ordering::Release);
    }
}

fn main() {
    let expected: Vec<f32> = (0..4800)
        .map(|index| {
            (0.9 * (2.0 * std::f64::consts::PI * 440.0 * index as f64 / 48_000.0).sin()) as f32
        })
        .collect();
    unsafe {
        let mut engine: *mut battuta_audio::ffi::Engine = std::ptr::null_mut();
        assert_eq!(
            battuta_audio::ffi::battuta_audio_engine_create(
                Some(on_event),
                std::ptr::null_mut(),
                &mut engine,
            ),
            0
        );
        let handle = battuta_audio::ffi::battuta_audio_sample_register(
            engine,
            expected.as_ptr(),
            expected.len() as u32,
        );
        assert_ne!(handle, 0);
        battuta_audio::ffi::battuta_audio_set_stream_property(
            engine,
            c"target.object".as_ptr(),
            c"VirtualSink".as_ptr(),
        );
        let started = battuta_audio::ffi::battuta_audio_stream_start(engine);
        println!("stream_start -> {started}");
        let deadline = Instant::now() + Duration::from_secs(6);
        while !STREAM_READY.load(Ordering::Acquire) && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        println!("stream ready: {}", STREAM_READY.load(Ordering::Acquire));
        let scheduled = battuta_audio::ffi::battuta_audio_schedule(engine, handle, 1.0, 1.0, 0);
        println!("schedule -> {scheduled}");
        std::thread::sleep(Duration::from_millis(400));
        let mut stats = battuta_audio::ffi::AudioStatsSnapshot {
            dropped_commands: 0,
            voice_steals: 0,
            underruns: 0,
            stream_recoveries: 0,
            actual_quantum: 0,
            rendered_frames: 0,
        };
        battuta_audio::ffi::battuta_audio_get_stats(engine, &mut stats);
        println!(
            "stats: rendered={} quantum={} underruns={} drops={}",
            stats.rendered_frames, stats.actual_quantum, stats.underruns, stats.dropped_commands
        );
        battuta_audio::ffi::battuta_audio_stream_stop(engine);
        battuta_audio::ffi::battuta_audio_engine_destroy(engine);
    }
}
