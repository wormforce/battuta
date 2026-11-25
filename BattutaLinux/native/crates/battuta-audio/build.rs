//! Links libpipewire-0.3 for the realtime output stream (feature `pipewire`).
//!
//! Uses `pkg-config` when available so distributions keep their header and
//! library layout; falls back to plain `-lpipewire-0.3` otherwise. No external
//! build dependencies — matching the workspace's hand-rolled style.

use std::process::Command;

fn pkg_config(args: &[&str]) -> Option<String> {
    Command::new("pkg-config")
        .args(args)
        .output()
        .ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_owned())
        .filter(|stdout| !stdout.is_empty())
}

fn main() {
    if !cfg!(feature = "pipewire") {
        return;
    }
    // Presence check first: a failing --libs means the dev package is missing
    // and the plain fallback below will produce the linker error.
    if pkg_config(&["--libs", "libpipewire-0.3"]).is_some() {
        if let Some(libdir) = pkg_config(&["--variable=libdir", "libpipewire-0.3"]) {
            println!("cargo:rustc-link-search=native={libdir}");
        }
        println!("cargo:rustc-link-lib=dylib=pipewire-0.3");
    } else {
        // No pkg-config or no .pc file: try the plain library name and let
        // the linker fail with a clear message if it is really missing.
        println!("cargo:rustc-link-lib=dylib=pipewire-0.3");
    }
    println!("cargo:rerun-if-changed=build.rs");
}
