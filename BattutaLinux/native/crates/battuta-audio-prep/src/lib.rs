//! Build-time converter for the canonical builtin audio assets (plan §6.2).
//!
//! Decodes the builtin MP3s, mean-downmixes to mono, resamples to 48 kHz
//! with cubic interpolation, applies the macOS-derived leading-silence trim
//! for keyboard samples, and writes deterministic BTP1 products plus a
//! manifest with SHA-256 hashes. The runtime hot path never decodes MP3.
//!
//! Documented divergence (plan §6.2 note): the Windows pipeline resamples
//! with WDL; this resampler is a Catmull-Rom cubic that is deterministic and
//! golden-tested on its own terms, not bit-identical to WDL. Trim semantics
//! mirror `LeadingSilenceTrimmer.cs` exactly.

use std::path::{Path, PathBuf};

use battuta_audio::SAMPLE_RATE;
use battuta_audio::mixer::cubic_interpolate;
use sha2::{Digest, Sha256};

pub const PRODUCT_MAGIC: u32 = 0x31505442; // "BTP1" little endian
pub const PRODUCT_EXTENSION: &str = "p48m";

/// Mirrors `LeadingSilenceTrimmer.cs` (f32 threshold semantics preserved).
pub mod trim {
    pub const SILENCE_THRESHOLD: f32 = 0.0008;
    pub const MAXIMUM_SCAN_DURATION_SECONDS: f64 = 0.25;
    pub const PRESERVED_PREROLL_DURATION_SECONDS: f64 = 0.00015;
    pub const MINIMUM_TRIM_DURATION_SECONDS: f64 = 0.0005;

    /// Returns the number of frames to drop, mirroring the C# finder.
    pub fn find_trim_frame_count(source: &[f32], sample_rate: u32) -> usize {
        if source.is_empty() {
            return 0;
        }

        let scan_frame_count = ((sample_rate as f64 * MAXIMUM_SCAN_DURATION_SECONDS).ceil()
            as usize)
            .min(source.len());
        let first_audible_frame =
            (0..scan_frame_count).find(|&frame| source[frame].abs() >= SILENCE_THRESHOLD);
        let Some(first_audible_frame) = first_audible_frame else {
            return 0;
        };

        let preroll_frames =
            (sample_rate as f64 * PRESERVED_PREROLL_DURATION_SECONDS).ceil() as usize;
        let trim_frames = first_audible_frame.saturating_sub(preroll_frames);
        let minimum_trim_frames =
            (sample_rate as f64 * MINIMUM_TRIM_DURATION_SECONDS).ceil() as usize;
        if trim_frames < minimum_trim_frames || trim_frames >= source.len() {
            return 0;
        }

        trim_frames
    }

    /// Applies the trim; keyboard samples only.
    pub fn trim(source: &[f32], sample_rate: u32) -> Vec<f32> {
        let trim_frames = find_trim_frame_count(source, sample_rate);
        if trim_frames == 0 {
            source.to_vec()
        } else {
            source[trim_frames..].to_vec()
        }
    }
}

/// Converts arbitrary decoded float PCM to the canonical 48 kHz mono form:
/// mean downmix across channels, then cubic resample when needed.
pub fn canonicalize(
    mono_or_interleaved: &[f32],
    source_channels: usize,
    source_rate: u32,
) -> Vec<f32> {
    assert!(source_channels > 0, "decoders never produce zero channels");
    let mono = if source_channels == 1 {
        mono_or_interleaved.to_vec()
    } else {
        mono_or_interleaved
            .chunks_exact(source_channels)
            .map(|frame| frame.iter().sum::<f32>() / source_channels as f32)
            .collect()
    };

    if source_rate == SAMPLE_RATE {
        return mono;
    }

    // Deterministic Catmull-Rom resample; output index i lands exactly on
    // source position i * source_rate / 48000.
    let position_step = source_rate as f64 / SAMPLE_RATE as f64;
    let output_frames = ((mono.len() as f64) / position_step).floor() as usize;
    let mut output = Vec::with_capacity(output_frames);
    for index in 0..output_frames {
        let position = index as f64 * position_step;
        output.push(cubic_interpolate(&mono, position));
    }
    output
}

/// Decodes a complete MP3 file to interleaved f32 at the container's rate.
pub fn decode_audio_bytes(
    bytes: &[u8],
    file_extension: &str,
) -> Result<(Vec<f32>, usize, u32), String> {
    use symphonia::core::codecs::audio::AudioDecoderOptions;
    use symphonia::core::errors::Error;
    use symphonia::core::formats::FormatOptions;
    use symphonia::core::formats::TrackType;
    use symphonia::core::formats::probe::Hint;
    use symphonia::core::io::MediaSourceStream;
    use symphonia::core::meta::MetadataOptions;

    let stream = MediaSourceStream::new(
        Box::new(std::io::Cursor::new(bytes.to_vec())),
        Default::default(),
    );
    let mut hint = Hint::new();
    hint.with_extension(file_extension);
    let mut format = symphonia::default::get_probe()
        .probe(
            &hint,
            stream,
            FormatOptions::default(),
            MetadataOptions::default(),
        )
        .map_err(|error| format!("probe failed: {error}"))?;
    let track = format
        .default_track(TrackType::Audio)
        .ok_or("no audio track")?;
    let track_id = track.id;
    let mut decoder = symphonia::default::get_codecs()
        .make_audio_decoder(
            track
                .codec_params
                .as_ref()
                .ok_or("missing codec params")?
                .audio()
                .ok_or("not an audio track")?,
            &AudioDecoderOptions::default(),
        )
        .map_err(|error| format!("decoder failed: {error}"))?;

    let mut pcm: Vec<f32> = Vec::new();
    let mut channels = 0usize;
    let mut sample_rate = 0u32;
    loop {
        // Symphonia 0.6 returns None at end of stream.
        let Some(packet) = format
            .next_packet()
            .map_err(|error| format!("packet failed: {error}"))?
        else {
            break;
        };
        if packet.track_id != track_id {
            continue;
        }
        match decoder.decode(&packet) {
            Ok(audio_buf) => {
                channels = audio_buf.spec().channels().count();
                sample_rate = audio_buf.spec().rate();
                let start = pcm.len();
                pcm.resize(start + audio_buf.samples_interleaved(), 0.0);
                audio_buf.copy_to_slice_interleaved(&mut pcm[start..]);
            }
            Err(Error::DecodeError(_)) => continue,
            Err(Error::IoError(error)) if error.kind() == std::io::ErrorKind::UnexpectedEof => {
                break;
            }
            Err(error) => return Err(format!("decode failed: {error}")),
        }
    }

    if pcm.is_empty() {
        return Err("decoded zero samples".to_owned());
    }
    if channels == 0 || sample_rate == 0 {
        return Err("decoder did not report stream format".to_owned());
    }
    Ok((pcm, channels, sample_rate))
}

/// Serializes one product in the BTP1 container the C# loader reads.
pub fn write_product_container(frames: &[f32]) -> Vec<u8> {
    let mut out = Vec::with_capacity(16 + frames.len() * 4);
    out.extend_from_slice(&PRODUCT_MAGIC.to_le_bytes());
    out.extend_from_slice(&SAMPLE_RATE.to_le_bytes());
    out.extend_from_slice(&1_u32.to_le_bytes());
    out.extend_from_slice(&(frames.len() as u32).to_le_bytes());
    for frame in frames {
        out.extend_from_slice(&frame.to_le_bytes());
    }
    out
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    let digest = hasher.finalize();
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// One manifest entry per converted asset.
pub struct ProductRecord {
    pub input_relative_path: String,
    pub input_sha256: String,
    pub output_relative_path: String,
    pub output_sha256: String,
    pub frame_count: u32,
    pub duration_seconds: f64,
}

/// Converts one builtin asset file into a product file.
///
/// `trim_leading_silence` is true for keyboard assets, false for pointer
/// assets, mirroring the C# catalog call sites.
pub fn convert_file(
    input_path: &Path,
    output_path: &Path,
    trim_leading_silence: bool,
) -> Result<ProductRecord, String> {
    let input_bytes = std::fs::read(input_path).map_err(|error| error.to_string())?;
    let input_sha256 = sha256_hex(&input_bytes);
    let extension = input_path
        .extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or("mp3");
    let (interleaved, channels, source_rate) = decode_audio_bytes(&input_bytes, extension)?;
    let mut mono = canonicalize(&interleaved, channels, source_rate);
    if trim_leading_silence {
        mono = trim::trim(&mono, SAMPLE_RATE);
    }
    if mono.is_empty() {
        return Err("product is empty".to_owned());
    }

    let container = write_product_container(&mono);
    let output_sha256 = sha256_hex(&container);
    if let Some(parent) = output_path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(output_path, &container).map_err(|error| error.to_string())?;

    let relative = |path: &Path| path.to_string_lossy().trim_start_matches("./").to_owned();
    Ok(ProductRecord {
        input_relative_path: relative(input_path),
        input_sha256,
        output_relative_path: relative(output_path),
        output_sha256,
        frame_count: mono.len() as u32,
        duration_seconds: mono.len() as f64 / SAMPLE_RATE as f64,
    })
}

/// Escapes a JSON string body (quotes + backslashes only appear in paths).
fn json_escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

/// Writes the manifest consumed by validation and (later) the C# loader.
pub fn write_manifest(manifest_path: &Path, records: &[ProductRecord]) -> Result<(), String> {
    let mut json =
        String::from("{\n  \"sample_rate\": 48000,\n  \"channels\": 1,\n  \"products\": [\n");
    for (index, record) in records.iter().enumerate() {
        json.push_str(&format!(
            "    {{\n      \"input\": \"{}\",\n      \"input_sha256\": \"{}\",\n      \"output\": \"{}\",\n      \"output_sha256\": \"{}\",\n      \"frame_count\": {},\n      \"duration_seconds\": {:.6}\n    }}{}\n",
            json_escape(&record.input_relative_path),
            record.input_sha256,
            json_escape(&record.output_relative_path),
            record.output_sha256,
            record.frame_count,
            record.duration_seconds,
            if index + 1 == records.len() { "" } else { "," },
        ));
    }
    json.push_str("  ]\n}\n");
    if let Some(parent) = manifest_path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(manifest_path, json).map_err(|error| error.to_string())
}

/// Walks the builtin source tree for decodable audio files (MP3/WAV),
/// sorted for determinism.
pub fn collect_audio_sources(source_root: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    let mut stack = vec![source_root.to_path_buf()];
    while let Some(directory) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else if path
                .extension()
                .is_some_and(|extension| extension == "mp3" || extension == "wav")
            {
                files.push(path);
            }
        }
    }
    files.sort();
    files
}
