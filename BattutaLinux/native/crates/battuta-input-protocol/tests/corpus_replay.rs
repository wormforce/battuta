//! Deterministic robustness replay (the plan's fuzz requirement, runnable on
//! stable CI): the decoder must never panic on arbitrary input. Committed
//! corpus files plus xorshift-generated buffers cover truncated frames,
//! oversized lengths, unknown messages, and garbage bytes.

use battuta_input_protocol::decode;
use std::path::PathBuf;

struct XorShift64(u64);

impl XorShift64 {
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next() % bound
    }
}

fn corpus_dir() -> PathBuf {
    let mut path = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    path.push("fuzz");
    path.push("corpus");
    path.push("decode_frame");
    path
}

#[test]
fn committed_corpus_never_panics() {
    let dir = corpus_dir();
    let entries = std::fs::read_dir(&dir)
        .unwrap_or_else(|error| panic!("corpus dir missing at {:?}: {error}", dir));

    let mut count = 0;
    for entry in entries {
        let path = entry.expect("readable corpus entry").path();
        if path.extension().is_none_or(|extension| extension != "bin") {
            continue;
        }

        let bytes = std::fs::read(&path).expect("readable corpus file");
        let _ = decode(&bytes); // must not panic; result may be Ok or Err
        count += 1;
    }

    assert!(
        count >= 6,
        "expected a non-trivial corpus, found {count} files"
    );
}

#[test]
fn deterministic_random_buffers_never_panic() {
    let mut rng = XorShift64(0xB477_4741_2026_0001);

    // Pure garbage.
    for _ in 0..20_000 {
        let length = rng.below(128) as usize;
        let buffer: Vec<u8> = (0..length).map(|_| (rng.next() & 0xFF) as u8).collect();
        let _ = decode(&buffer);
    }

    // Valid magic with random bodies: exercises payload-length paths.
    for _ in 0..20_000 {
        let length = rng.below(64) as usize;
        let mut buffer = Vec::with_capacity(4 + length);
        buffer.extend_from_slice(&battuta_input_protocol::MAGIC);
        buffer.extend_from_slice(&[rng.below(256) as u8, rng.below(256) as u8]);
        buffer.extend_from_slice(&(rng.below(8192) as u16).to_le_bytes());
        for _ in 0..length {
            buffer.push((rng.next() & 0xFF) as u8);
        }
        let _ = decode(&buffer);
    }

    // Truncations of a valid heartbeat frame at every prefix length.
    let heartbeat = battuta_input_protocol::Message::Heartbeat {
        sequence: 12,
        monotonic_ns: 100_000_200_000,
    };
    let frame = battuta_input_protocol::encode_frame(&heartbeat).unwrap();
    for prefix in 0..frame.len() {
        let _ = decode(&frame[..prefix]);
    }
}
