#![no_main]

//! Fuzz target for the broker frame decoder. Run with:
//! `cargo +nightly fuzz run decode_frame -- -max_total_time=300`
//! from BattutaLinux/native/crates/battuta-input-protocol.

use battuta_input_protocol::decode;

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    // The decoder must never panic; any outcome (Ok or Err) is acceptable.
    let _ = decode(data);
});
