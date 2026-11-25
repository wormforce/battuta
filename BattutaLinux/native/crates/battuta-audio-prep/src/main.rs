//! CLI entry: converts a builtin asset tree into deterministic BTP1 products.
//!
//! ```text
//! cargo run -p battuta-audio-prep -- --source <builtin dir> --out <product dir>
//! ```
//!
//! Keyboard profile directories (press/release phases) get the leading
//! silence trim; pointer profiles do not, mirroring the C# catalog.

use std::path::{Path, PathBuf};

use battuta_audio_prep::{ProductRecord, collect_audio_sources, convert_file, write_manifest};

fn parse_arg(args: &[String], key: &str) -> Option<PathBuf> {
    args.iter()
        .position(|argument| argument == key)
        .and_then(|index| args.get(index + 1))
        .map(PathBuf::from)
}

/// Keyboard samples live in `<profile>/<press|release>/...` — everything
/// else (pointer) is scheduled without trimming, like the C# catalog.
fn is_keyboard_asset(relative_path: &Path) -> bool {
    relative_path.components().any(|component| {
        let name = component.as_os_str().to_string_lossy();
        name == "press" || name == "release"
    })
}

fn main() -> Result<(), String> {
    let args: Vec<String> = std::env::args().collect();
    let source_root = parse_arg(&args, "--source")
        .ok_or("usage: battuta-audio-prep --source <builtin dir> --out <product dir>")?;
    let out_root = parse_arg(&args, "--out")
        .ok_or("usage: battuta-audio-prep --source <builtin dir> --out <product dir>")?;

    let sources = collect_audio_sources(&source_root);
    if sources.is_empty() {
        return Err(format!(
            "no MP3/WAV sources under {}",
            source_root.display()
        ));
    }

    let mut records = Vec::with_capacity(sources.len());
    for source in &sources {
        let relative = source
            .strip_prefix(&source_root)
            .map_err(|_| "source outside root")?
            .to_path_buf();
        let product_relative = relative.with_extension("p48m");
        let output_path = out_root.join(&product_relative);
        let record = convert_file(source, &output_path, is_keyboard_asset(&relative))?;
        println!(
            "{} -> {} ({} frames)",
            relative.display(),
            product_relative.display(),
            record.frame_count
        );
        records.push(ProductRecord {
            input_relative_path: relative.to_string_lossy().replace('\\', "/"),
            input_sha256: record.input_sha256,
            output_relative_path: product_relative.to_string_lossy().replace('\\', "/"),
            output_sha256: record.output_sha256,
            frame_count: record.frame_count,
            duration_seconds: record.duration_seconds,
        });
    }

    write_manifest(&out_root.join("manifest.json"), &records)?;
    println!(
        "wrote {} products + manifest to {}",
        records.len(),
        out_root.display()
    );
    Ok(())
}
