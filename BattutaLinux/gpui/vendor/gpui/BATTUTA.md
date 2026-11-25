# GPUI 0.2.2 for Battuta

This copy comes from the [crates.io release](https://crates.io/crates/gpui/0.2.2)
under Apache-2.0. The published crate has SHA-256
`979b45cfa6ec723b6f42330915a1b3769b930d02b2d505f9697f8ca602bee707`.

- `Cargo.toml` omits the published examples and integration tests.
- The Linux Wayland and X11 clients keep their event loop running after the
  last window closes so that Battuta can keep its tray available.
- `src/platform.rs`, `src/window.rs` and the Wayland window implementation
  allow a window to use a desktop shell's XDG activation token.
- `src/taffy.rs` uses explicit types for two grid sizing literals for
  compatibility with newer rustc versions.

When updating GPUI, use the upstream explicit quit mode once the pinned
version exposes it.
