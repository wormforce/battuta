#!/usr/bin/env bash
# Generates the build-time PCM products (.p48m) next to the app output's
# canonical sources, so PreparedAudioProductLoader resolves them (plan §6.2;
# packaging wiring comes in PR 10 — this script is the dev-loop equivalent).
#
# Usage: scripts/generate-prep-audio.sh [dotnet-output-dir]
#        (defaults to src/Battuta.Linux/bin/Debug/net10.0)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out_dir="${1:-$repo_root/BattutaLinux/src/Battuta.Linux/bin/Debug/net10.0}"

source_root="$repo_root/shared/audio/builtin"
prep_binary="$repo_root/BattutaLinux/native/target/release/battuta-audio-prep"
[[ -x "$prep_binary" ]] || prep_binary="$repo_root/BattutaLinux/native/target/debug/battuta-audio-prep"
if [[ ! -x "$prep_binary" ]]; then
    echo "error: battuta-audio-prep not built; run:" >&2
    echo "  cargo build -p battuta-audio-prep --manifest-path BattutaLinux/native/Cargo.toml" >&2
    exit 1
fi

if [[ ! -d "$out_dir/Assets/Sounds" ]]; then
    echo "error: $out_dir/Assets/Sounds not found; build Battuta.Linux first" >&2
    exit 1
fi

echo "generating PCM products: $source_root -> $out_dir/Assets/Sounds"
"$prep_binary" --source "$source_root" --out "$out_dir/Assets/Sounds"
echo "done."
