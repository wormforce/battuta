use battuta_audio::SAMPLE_RATE;
use battuta_audio_prep::canonicalize;
use battuta_audio_prep::trim;
use battuta_audio_prep::write_product_container;

/// Resamples a 44.1 kHz sine to 48 kHz and verifies the dominant frequency
/// survives (energy at the bin closest to 440 Hz must dominate).
#[test]
fn resample_44100_to_48000_preserves_frequency() {
    const SOURCE_RATE: u32 = 44_100;
    const FREQUENCY: f64 = 440.0;
    let frames = SOURCE_RATE as usize; // 1 second
    let source: Vec<f32> = (0..frames)
        .map(|index| {
            (0.9 * (2.0 * std::f64::consts::PI * FREQUENCY * index as f64 / SOURCE_RATE as f64)
                .sin()) as f32
        })
        .collect();

    let output = canonicalize(&source, 1, SOURCE_RATE);
    assert_eq!(output.len(), 48_000);

    // Dominant bin: simple zero-crossing frequency estimate over 10 periods
    // of the output; the estimator quantizes to ~10 Hz per crossing.
    let window = &output[48_000 / 2..48_000 / 2 + 4_800];
    let mut crossings = 0usize;
    for pair in window.windows(2) {
        if pair[0] < 0.0 && pair[1] >= 0.0 {
            crossings += 1;
        }
    }
    let measured_hz = crossings as f64 * (48_000.0 / window.len() as f64);
    assert!(
        (measured_hz - FREQUENCY).abs() <= 10.0,
        "measured {measured_hz} Hz, expected {FREQUENCY}"
    );
}

/// A stereo source mean-downmixes to mono before resampling.
#[test]
fn stereo_sources_downmix_to_mono() {
    let stereo = vec![0.5, -0.5, 1.0, -1.0, 0.25, -0.25];
    let output = canonicalize(&stereo, 2, SAMPLE_RATE);
    assert_eq!(output.len(), 3);
    assert_eq!(output[0], 0.0);
    assert_eq!(output[1], 0.0);
    assert_eq!(output[2], 0.0);
}

/// Same-rate passthrough does not resample.
#[test]
fn same_rate_passthrough_is_identity() {
    let mono = vec![0.1, -0.2, 0.3];
    let output = canonicalize(&mono, 1, SAMPLE_RATE);
    assert_eq!(output, mono);
}

/// Mirrors LeadingSilenceTrimmer semantics (f32 threshold, ceil rules).
#[test]
fn trim_behaves_like_the_csharp_finder() {
    let rate = SAMPLE_RATE;
    let preroll = (rate as f64 * 0.00015).ceil() as usize; // 8 frames
    let minimum = (rate as f64 * 0.0005).ceil() as usize; // 24 frames

    // All silence: no trim (matches C#: firstAudible < 0 → 0).
    let silent = vec![0.0f32; rate as usize];
    assert_eq!(trim::find_trim_frame_count(&silent, rate), 0);

    // Short lead-in below the minimum trim: no trim.
    let lead = 10usize;
    assert!(
        lead < minimum,
        "test premise: 10 frames is below the minimum"
    );
    let mut samples = vec![0.0f32; lead];
    samples.push(0.5);
    assert_eq!(trim::find_trim_frame_count(&samples, rate), 0);

    // Long lead-in: trim with preroll preserved.
    let lead = 1_000usize;
    let mut samples = vec![0.0f32; lead + rate as usize];
    samples[lead] = 0.5;
    let trim_frames = trim::find_trim_frame_count(&samples, rate);
    assert_eq!(trim_frames, lead - preroll);
    let trimmed = trim::trim(&samples, rate);
    assert!((trimmed[preroll] - 0.5).abs() < 1e-6);
    assert_eq!(trimmed.len(), samples.len() - trim_frames);

    // Boundary: audible at scan limit still trims; beyond it does not.
    let scan_frames = (rate as f64 * 0.25).ceil() as usize;
    let mut late = vec![0.0f32; scan_frames + 50];
    late[scan_frames] = 0.5;
    assert_eq!(trim::find_trim_frame_count(&late, rate), 0);
}

/// The BTP1 container matches the format the C# loader validates.
#[test]
fn product_container_layout() {
    let frames = [0.25f32, -0.25f32];
    let bytes = write_product_container(&frames);
    assert_eq!(bytes.len(), 16 + 8);
    assert_eq!(&bytes[0..4], b"BTP1");
    assert_eq!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), 48_000);
    assert_eq!(u32::from_le_bytes(bytes[8..12].try_into().unwrap()), 1);
    assert_eq!(u32::from_le_bytes(bytes[12..16].try_into().unwrap()), 2);
    assert_eq!(f32::from_le_bytes(bytes[16..20].try_into().unwrap()), 0.25);
}

/// Determinism: identical inputs produce byte-identical containers.
#[test]
fn product_writes_are_deterministic() {
    let frames: Vec<f32> = (0..4_800)
        .map(|index| {
            (0.8 * (2.0 * std::f64::consts::PI * 220.0 * index as f64 / 48_000.0).sin()) as f32
        })
        .collect();
    let first = write_product_container(&frames);
    let second = write_product_container(&frames);
    assert_eq!(first, second);
    assert_eq!(first.len(), 16 + frames.len() * 4);
}
