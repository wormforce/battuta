//! Virtual-sink integration test (plan §6.1/PR 6 acceptance): boots a
//! private PipeWire daemon with a null sink, runs the native engine against
//! it, records the sink's monitor with `pw-cat`, and compares the captured
//! waveform against the scheduled sample.
//!
//! Skips when `pipewire`/`pw-cat` are unavailable (e.g. minimal CI images).
//! The tests mutate the process-wide `XDG_RUNTIME_DIR`; a file-local mutex
//! serializes them so any test-thread count works.

use std::process::{Child, Command};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

const SINE_FRAMES: usize = 4_800; // 100 ms at 48 kHz
const SINE_FREQUENCY: f64 = 440.0;

struct ChildGuard(Child);

impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

static STREAM_READY: AtomicBool = AtomicBool::new(false);
static STREAM_ERROR: AtomicBool = AtomicBool::new(false);
static DEVICE_CHANGED: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// Both tests mutate the process-wide `XDG_RUNTIME_DIR`; the lock makes the
/// file safe under any test-thread count (the historical advice to run with
/// `--test-threads=1` is no longer load-bearing).
static E2E_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

unsafe extern "C" fn on_event(event: i32, _user_data: *mut std::ffi::c_void) {
    if event == 1 {
        STREAM_READY.store(true, Ordering::Release);
    } else if event == 2 {
        STREAM_ERROR.store(true, Ordering::Release);
    } else if event == 3 {
        DEVICE_CHANGED.fetch_add(1, Ordering::Release);
    }
}

fn reset_events() {
    STREAM_READY.store(false, Ordering::Release);
    STREAM_ERROR.store(false, Ordering::Release);
    DEVICE_CHANGED.store(0, Ordering::Release);
}

#[test]
fn pipewire_stream_plays_the_sample_into_a_virtual_sink() {
    let Some(pipewire_path) = which("pipewire") else {
        eprintln!("skipping: pipewire binary not found");
        return;
    };
    let Some(pw_cat_path) = which("pw-cat") else {
        eprintln!("skipping: pw-cat binary not found");
        return;
    };
    let _serial = E2E_LOCK.lock().expect("e2e lock poisoned");
    reset_events();

    let work = std::env::temp_dir().join(format!(
        "battuta-pw-e2e-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .subsec_nanos()
    ));
    std::fs::create_dir_all(work.join("runtime")).expect("create work dir");
    let runtime_dir = work.join("runtime");
    let config_path = work.join("pipewire.conf");
    std::fs::write(&config_path, daemon_config()).expect("write daemon config");
    // Isolate from the user session: the engine (this process) resolves the
    // daemon socket under XDG_RUNTIME_DIR. SAFETY: single-threaded test
    // binary (run with --test-threads=1) and no other threads yet.
    unsafe {
        std::env::set_var("XDG_RUNTIME_DIR", &runtime_dir);
    }

    // 1. Private daemon with a null sink named VirtualSink. The daemon gets
    //    its own environment (Command::env is child-scoped) so its daemon
    //    config never leaks into this process's PipeWire client context.
    let daemon_log = work.join("daemon.log");
    let daemon = Command::new(&pipewire_path)
        .env("PIPEWIRE_CONFIG_NAME", &config_path)
        .env("XDG_RUNTIME_DIR", &runtime_dir)
        .stdout(
            std::fs::OpenOptions::new()
                .create(true)
                .write(true)
                .truncate(true)
                .open(&daemon_log)
                .unwrap(),
        )
        .stderr(
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&daemon_log)
                .unwrap(),
        )
        .spawn()
        .expect("spawn pipewire daemon");
    let _daemon = ChildGuard(daemon);
    let socket = runtime_dir.join("pipewire-0");
    wait_for(&socket, Duration::from_secs(8)).expect("private pipewire socket");

    // 1b. Session manager (WirePlumber) against the private daemon: without
    //     it nothing creates links between the stream and the sink. It gets
    //     only XDG_RUNTIME_DIR so its own default configuration applies.
    let wireplumber_log = work.join("wireplumber.log");
    let _wireplumber = match which("wireplumber") {
        Some(path) => Some(ChildGuard(
            Command::new(path)
                .env("XDG_RUNTIME_DIR", &runtime_dir)
                .stdout(
                    std::fs::OpenOptions::new()
                        .create(true)
                        .write(true)
                        .truncate(true)
                        .open(&wireplumber_log)
                        .unwrap(),
                )
                .stderr(
                    std::fs::OpenOptions::new()
                        .create(true)
                        .append(true)
                        .open(&wireplumber_log)
                        .unwrap(),
                )
                .spawn()
                .expect("spawn wireplumber"),
        )),
        None => {
            eprintln!("wireplumber not found; auto-linking will rely on the daemon");
            None
        }
    };
    // Give the session manager time to designate the default sink.
    std::thread::sleep(Duration::from_millis(1200));

    // 2. Capture the VirtualSink monitor while the engine plays.
    let capture_path = work.join("capture.f32");
    let capture_log = work.join("pwcat.log");
    let capture = Command::new(&pw_cat_path)
        .args([
            "--record",
            "--raw",
            "--format=f32",
            "--rate=48000",
            "--channels=2",
            "--target=VirtualSink",
            // Recording a sink means capturing its monitor ports; the
            // WirePlumber policy needs this property to allow the direction.
            "-P",
            "stream.capture.sink=true",
            capture_path.to_str().unwrap(),
        ])
        .stdout(
            std::fs::OpenOptions::new()
                .create(true)
                .write(true)
                .truncate(true)
                .open(&capture_log)
                .unwrap(),
        )
        .stderr(
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&capture_log)
                .unwrap(),
        )
        .spawn()
        .expect("spawn pw-cat recorder");
    let _capture = ChildGuard(capture);
    std::thread::sleep(Duration::from_millis(300));

    // 3. Engine: register a sine sample, start the stream, play once.
    let expected = sine(SINE_FRAMES, SINE_FREQUENCY, 0.9);
    let mut engine_handle: *mut battuta_audio::ffi::Engine = std::ptr::null_mut();
    // SAFETY: plain engine creation with a valid callback.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_engine_create(
                Some(on_event),
                std::ptr::null_mut(),
                &mut engine_handle,
            ),
            0,
            "engine create"
        );
        assert_eq!(
            battuta_audio::ffi::battuta_audio_set_stream_property(
                engine_handle,
                c"target.object".as_ptr(),
                c"VirtualSink".as_ptr(),
            ),
            0
        );
    }

    let sample_handle = unsafe {
        // SAFETY: expected is alive for the duration of the call.
        battuta_audio::ffi::battuta_audio_sample_register(
            engine_handle,
            expected.as_ptr(),
            expected.len() as u32,
        )
    };
    assert_ne!(sample_handle, 0, "sample registration");

    // SAFETY: valid engine handle from above.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_stream_start(engine_handle),
            0,
            "stream start"
        );
    }
    let ready_deadline = Instant::now() + Duration::from_secs(8);
    while !STREAM_READY.load(Ordering::Acquire) && Instant::now() < ready_deadline {
        std::thread::sleep(Duration::from_millis(10));
    }
    assert!(
        STREAM_READY.load(Ordering::Acquire),
        "stream must reach the streaming state; wireplumber log: {}",
        std::fs::read_to_string(&wireplumber_log).unwrap_or_default()
    );

    // SAFETY: valid engine handle.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_schedule(
                engine_handle,
                sample_handle,
                1.0,
                1.0,
                0, // keyboard priority
            ),
            1,
            "schedule the sine"
        );
    }
    std::thread::sleep(Duration::from_millis(500));

    let mut stats = battuta_audio::ffi::AudioStatsSnapshot {
        dropped_commands: 0,
        voice_steals: 0,
        underruns: 0,
        stream_recoveries: 0,
        actual_quantum: 0,
        rendered_frames: 0,
    };
    // SAFETY: valid engine handle and stats pointer.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_get_stats(engine_handle, &mut stats),
            0
        );
        battuta_audio::ffi::battuta_audio_stream_stop(engine_handle);
        battuta_audio::ffi::battuta_audio_sample_release(engine_handle, sample_handle);
        battuta_audio::ffi::battuta_audio_engine_destroy(engine_handle);
    }
    assert!(
        stats.rendered_frames > SINE_FRAMES as u64,
        "engine must have rendered the sample, got {}",
        stats.rendered_frames
    );
    assert!(
        (32..=2048).contains(&stats.actual_quantum),
        "unexpected quantum {}",
        stats.actual_quantum
    );
    assert!(!STREAM_ERROR.load(Ordering::Acquire), "stream error event");

    // 4. Compare the captured waveform against the scheduled sample.
    std::thread::sleep(Duration::from_millis(300));
    let captured = std::fs::read(&capture_path).unwrap_or_default();
    let frames: Vec<f32> = captured
        .as_chunks::<4>()
        .0
        .iter()
        .copied()
        .map(f32::from_le_bytes)
        .collect();
    let stereo: Vec<f32> = frames
        .iter()
        .enumerate()
        .filter(|(index, _)| index % 2 == 0)
        .map(|(_, value)| *value)
        .collect();
    if stereo.len() < SINE_FRAMES {
        panic!(
            "capture too short: {} frames; pwcat log: {}",
            stereo.len(),
            std::fs::read_to_string(&capture_log).unwrap_or_default()
        );
    }

    let (offset, error) = best_alignment(&stereo, &expected);
    eprintln!(
        "capture: {} frames, aligned at {:?}, mean abs error {error:.5}, quantum {}",
        stereo.len(),
        offset,
        stats.actual_quantum
    );
    assert!(
        offset.is_some(),
        "the sine must appear in the captured monitor stream"
    );
    assert!(
        error < 0.05,
        "golden waveform mismatch after alignment: {error}"
    );

    let _ = std::fs::remove_dir_all(&work);
}

/// Default-device-follow (plan §6.1/§6.3): the engine must report
/// `EVENT_DEVICE_CHANGED` when the daemon's default audio sink changes, and
/// the host rebuild must recover into the streaming state afterwards.
#[test]
fn pipewire_reports_default_output_device_changes() {
    let Some(pipewire_path) = which("pipewire") else {
        eprintln!("skipping: pipewire binary not found");
        return;
    };
    let Some(pw_metadata_path) = which("pw-metadata") else {
        eprintln!("skipping: pw-metadata binary not found");
        return;
    };
    let _serial = E2E_LOCK.lock().expect("e2e lock poisoned");
    reset_events();

    let work = std::env::temp_dir().join(format!(
        "battuta-pw-devmon-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .subsec_nanos()
    ));
    std::fs::create_dir_all(work.join("runtime")).expect("create work dir");
    let runtime_dir = work.join("runtime");
    let config_path = work.join("pipewire.conf");
    std::fs::write(&config_path, daemon_config()).expect("write daemon config");

    unsafe {
        std::env::set_var("XDG_RUNTIME_DIR", &runtime_dir);
    }

    // Private daemon with two sinks, then the session manager.
    let daemon_log = work.join("daemon.log");
    let daemon = Command::new(&pipewire_path)
        .env("PIPEWIRE_CONFIG_NAME", &config_path)
        .env("XDG_RUNTIME_DIR", &runtime_dir)
        .stdout(
            std::fs::OpenOptions::new()
                .create(true)
                .write(true)
                .truncate(true)
                .open(&daemon_log)
                .unwrap(),
        )
        .stderr(
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&daemon_log)
                .unwrap(),
        )
        .spawn()
        .expect("spawn pipewire daemon");
    let _daemon = ChildGuard(daemon);
    let socket = runtime_dir.join("pipewire-0");
    wait_for(&socket, Duration::from_secs(8)).expect("private pipewire socket");

    let wireplumber_log = work.join("wireplumber.log");
    let _wireplumber = match which("wireplumber") {
        Some(path) => Some(ChildGuard(
            Command::new(path)
                .env("XDG_RUNTIME_DIR", &runtime_dir)
                .stdout(
                    std::fs::OpenOptions::new()
                        .create(true)
                        .write(true)
                        .truncate(true)
                        .open(&wireplumber_log)
                        .unwrap(),
                )
                .stderr(
                    std::fs::OpenOptions::new()
                        .create(true)
                        .append(true)
                        .open(&wireplumber_log)
                        .unwrap(),
                )
                .spawn()
                .expect("spawn wireplumber"),
        )),
        None => {
            eprintln!("wireplumber not found; relying on daemon-side default routing");
            None
        }
    };
    std::thread::sleep(Duration::from_millis(1200));

    // Seed the default metadata so the monitor's initial dump records the
    // baseline (initial state must not raise the event).
    let set_default = |sink: &str| {
        let value = format!(r#"{{ name: "{sink}" }}"#);
        let log_path = work.join("pwmetadata.log");
        let status = Command::new(&pw_metadata_path)
            .env("XDG_RUNTIME_DIR", &runtime_dir)
            .args(["-n", "default", "0", "default.audio.sink", &value])
            .stdout(
                std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(&log_path)
                    .unwrap(),
            )
            .stderr(
                std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(&log_path)
                    .unwrap(),
            )
            .status()
            .expect("run pw-metadata");
        assert!(status.success(), "pw-metadata failed for {sink}");
    };
    set_default("VirtualSink");
    std::thread::sleep(Duration::from_millis(500));

    // Engine with default routing (no target override): production shape.
    let mut engine_handle: *mut battuta_audio::ffi::Engine = std::ptr::null_mut();
    // SAFETY: plain engine creation with a valid callback.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_engine_create(
                Some(on_event),
                std::ptr::null_mut(),
                &mut engine_handle,
            ),
            0,
            "engine create"
        );
        assert_eq!(
            battuta_audio::ffi::battuta_audio_stream_start(engine_handle),
            0,
            "stream start"
        );
    }
    let ready_deadline = Instant::now() + Duration::from_secs(8);
    while !STREAM_READY.load(Ordering::Acquire) && Instant::now() < ready_deadline {
        std::thread::sleep(Duration::from_millis(10));
    }
    assert!(
        STREAM_READY.load(Ordering::Acquire),
        "stream must reach the streaming state; wireplumber log: {}",
        std::fs::read_to_string(&wireplumber_log).unwrap_or_default()
    );

    // Let the monitor bind and dump the baseline.
    std::thread::sleep(Duration::from_millis(700));
    let baseline = DEVICE_CHANGED.load(Ordering::Acquire);

    // Switch the default sink; the monitor must raise EVENT_DEVICE_CHANGED.
    set_default("VirtualSinkB");
    let event_deadline = Instant::now() + Duration::from_secs(5);
    while DEVICE_CHANGED.load(Ordering::Acquire) == baseline && Instant::now() < event_deadline {
        std::thread::sleep(Duration::from_millis(20));
    }
    assert!(
        DEVICE_CHANGED.load(Ordering::Acquire) > baseline,
        "no EVENT_DEVICE_CHANGED after the default sink changed (baseline {baseline}); \
         wireplumber log: {}",
        std::fs::read_to_string(&wireplumber_log).unwrap_or_default()
    );

    // The host answers the event with a stream rebuild; the PCM bank is
    // untouched and the stream recovers into the streaming state.
    // SAFETY: valid engine handle.
    let rebuild = unsafe { battuta_audio::ffi::battuta_audio_stream_rebuild(engine_handle) };
    assert_eq!(rebuild, 0, "stream rebuild after device change");
    std::thread::sleep(Duration::from_millis(300));
    let mut stats = battuta_audio::ffi::AudioStatsSnapshot {
        dropped_commands: 0,
        voice_steals: 0,
        underruns: 0,
        stream_recoveries: 0,
        actual_quantum: 0,
        rendered_frames: 0,
    };
    // SAFETY: valid engine handle.
    unsafe {
        assert_eq!(
            battuta_audio::ffi::battuta_audio_get_stats(engine_handle, &mut stats),
            0
        );
        assert_eq!(
            stats.stream_recoveries, 1,
            "exactly one rebuild recovery after the device change"
        );
        battuta_audio::ffi::battuta_audio_stream_stop(engine_handle);
        battuta_audio::ffi::battuta_audio_engine_destroy(engine_handle);
    }
    assert!(
        !STREAM_ERROR.load(Ordering::Acquire),
        "stream error event during the device change"
    );

    let _ = std::fs::remove_dir_all(&work);
}

fn best_alignment(captured: &[f32], expected: &[f32]) -> (Option<usize>, f64) {
    // Find the onset (first loud frame), then try small alignments around it.
    let loud = |value: f32| value.abs() > 0.25;
    let onset = captured.iter().position(|value| loud(*value));
    let Some(onset) = onset else {
        return (None, 1.0);
    };
    let mut best: Option<(usize, f64)> = None;
    for offset in onset.saturating_sub(64)..=(onset + 64).min(captured.len() - expected.len()) {
        let mut total = 0.0_f64;
        for (index, expected_value) in expected.iter().enumerate() {
            total += (captured[offset + index] - expected_value).abs() as f64;
        }
        let error = total / expected.len() as f64;
        if best.is_none_or(|(_, best_error)| error < best_error) {
            best = Some((offset, error));
        }
    }
    match best {
        Some((offset, error)) => (Some(offset), error),
        None => (None, 1.0),
    }
}

fn sine(frames: usize, frequency: f64, amplitude: f64) -> Vec<f32> {
    (0..frames)
        .map(|index| {
            (amplitude * (2.0 * std::f64::consts::PI * frequency * index as f64 / 48_000.0).sin())
                as f32
        })
        .collect()
}

fn daemon_config() -> String {
    String::from(
        r#"
context.properties = {
    core.daemon = true
    core.name = pipewire-0
    default.clock.rate = 48000
    default.clock.quantum = 128
}
context.spa-libs = {
    support.* = support/libspa-support
    audioconvert.* = audioconvert/libspa-audioconvert
}
context.modules = [
    { name = libpipewire-module-protocol-native }
    { name = libpipewire-module-client-node }
    { name = libpipewire-module-client-device }
    { name = libpipewire-module-spa-node-factory }
    { name = libpipewire-module-spa-device-factory }
    { name = libpipewire-module-adapter }
    { name = libpipewire-module-link-factory }
    { name = libpipewire-module-access }
    { name = libpipewire-module-metadata }
    { name = libpipewire-module-rt, flags = [ ifexists nofail ] }
]
context.objects = [
    { factory = spa-node-factory, args = { factory.name = support.node.driver node.name = Dummy-Driver node.group = d0 priority.driver = 10000 } }
    { factory = adapter, args = { factory.name = support.null-audio-sink node.name = VirtualSink node.description = BattutaTestSink media.class = Audio/Sink audio.position = [ FL, FR ] } }
    { factory = adapter, args = { factory.name = support.null-audio-sink node.name = VirtualSinkB node.description = BattutaTestSinkB media.class = Audio/Sink audio.position = [ FL, FR ] } }
]
context.exec = [
]
"#,
    )
}

fn wait_for(path: &std::path::Path, timeout: Duration) -> Result<(), String> {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if path.exists() {
            return Ok(());
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    Err(format!("timeout waiting for {}", path.display()))
}

fn which(name: &str) -> Option<std::path::PathBuf> {
    let path = std::env::var_os("PATH")?;
    for directory in std::env::split_paths(&path) {
        let candidate = directory.join(name);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}
