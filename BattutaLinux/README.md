# Battuta on Linux

The desktop client uses GPUI on native Wayland and supports X11 in X11 desktop
sessions. It uses the same built-in sound packs and brand assets as the other
desktop clients. Keyboard and mouse sounds, local typing statistics, and the
DIY sound pack editor are available from the control panel.

Launch **Battuta** from the application menu or run `battuta`. Closing the
window keeps audio and statistics running. Click the StatusNotifierItem tray
icon or launch Battuta again to bring the window back. Use **Quit** in the tray
menu to stop the application.

## Build

The build needs Rust, .NET SDK 10, Python 3, Node.js, ffmpeg, a C toolchain, and
development packages for PipeWire, Fontconfig, XCB, and xkbcommon. The GPUI
client needs a Vulkan driver. Compressed DIY audio imports use ffmpeg.

From the repository root:

```sh
./BattutaLinux/scripts/run-gpui.sh
./BattutaLinux/scripts/build-linux-package.sh --version 0.1.0
```

The package script builds an x86-64 tarball and builds a DEB or RPM when the
corresponding native packaging tool is installed. Install the native package
on a systemd and polkit desktop to set up the on-demand
`battuta-inputd.socket` service and the application menu entry. A development
build can use the socket provided by an installed package.

## Input and local data

The broker sends physical key and button identifiers and transition events to
the active local session. It filters input when the session is locked. The
application saves optional aggregate counts and top application names on the
device. Key sequences can reveal typed text even though input is not converted
to characters. The broker does not send input to a server. The optional update
check requests public release metadata from GitHub.

User settings are under `${XDG_CONFIG_HOME:-~/.config}/battuta`. Statistics
and custom sound packs live under `${XDG_DATA_HOME:-~/.local/share}/battuta`.
Foreground application attribution depends on compositor support. GNOME Shell
may ask for permission to enable Battuta's companion extension.

## Code and tests

- `gpui/` contains the window, tray, and the client for the private runtime pipe.
- `src/` contains the .NET host, desktop adapters, and view models.
- `native/` contains the input broker, PipeWire shim, and PCM preparation tool.
- `shared/dotnet/Battuta.Engine` contains the Linux engine adapters. Its project
  also compiles portable sources already used by Windows into the Linux build.
  The source links keep the existing Windows project layout and use the same
  implementations for statistics, package handling, and other portable code.

```sh
dotnet test --project BattutaLinux/tests/Battuta.Linux.Tests/Battuta.Linux.Tests.csproj
dotnet test --project shared/dotnet/Battuta.Engine.Tests/Battuta.Engine.Tests.csproj
cargo test --locked --manifest-path BattutaLinux/native/Cargo.toml
```
