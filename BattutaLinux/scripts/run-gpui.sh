#!/usr/bin/env bash
set -euo pipefail

linux_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime_dir="$linux_root/src/Battuta.Linux/bin/Debug/net10.0"
dotnet="${DOTNET:-dotnet}"
if ! command -v "$dotnet" >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet="$HOME/.dotnet/dotnet"
fi
if [[ -z "${DOTNET_ROOT:-}" ]]; then
    DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v "$dotnet")")")"
    export DOTNET_ROOT
fi

"$dotnet" build "$linux_root/src/Battuta.Linux/Battuta.Linux.csproj" --nologo -v quiet
cargo build --locked --manifest-path "$linux_root/native/Cargo.toml" -p battuta-audio -p battuta-audio-prep
install -m 0755 "$linux_root/native/target/debug/libbattuta_audio.so" "$runtime_dir/libbattuta_audio.so"
"$linux_root/native/target/debug/battuta-audio-prep" \
    --source "$linux_root/../shared/audio/builtin" --out "$runtime_dir/Assets/Sounds"
export BATTUTA_RUNTIME="$runtime_dir/Battuta.Linux"
exec cargo run --locked --manifest-path "$linux_root/gpui/Cargo.toml"
