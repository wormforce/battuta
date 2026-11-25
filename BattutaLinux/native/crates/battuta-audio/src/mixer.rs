//! Sixteen-voice polyphonic mixer — a line-faithful port of the Windows
//! `PolyphonicSampleProvider` render semantics so a given command transcript
//! renders identical samples on both platforms (locked by the golden
//! fixture in `shared/contracts/fixtures/audio/`).
//!
//! Threading model:
//!
//! - [`Mixer::try_schedule`] and [`Mixer::reset_for_output_restart`] are the
//!   control/host side (`&self`, atomics only, no allocation).
//! - [`Mixer::render`] runs on the single PipeWire realtime thread; it is
//!   the **only** code that touches the voice array, which is why the array
//!   sits in an `UnsafeCell`. Exactly one RT thread exists because exactly
//!   one stream process callback exists (plan §6.1).
//! - Samples are referenced by bank slot; the bank refcount keeps memory
//!   alive (see [`crate::bank`]).

use std::cell::UnsafeCell;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use crate::bank::SampleBank;
use crate::ring::SpscCommandRing;
use crate::stats::AudioStats;
use crate::{MAXIMUM_PLAYBACK_RATE, MINIMUM_PLAYBACK_RATE, OUTPUT_CHANNELS, VOICE_COUNT};

/// One queued voice start. `sample` is a bank slot handle. Field order and
/// meaning mirror the Windows `StartVoiceCommand` record.
#[derive(Clone, Copy)]
pub struct StartVoiceCommand {
    pub sample: u32,
    pub gain: f32,
    pub rate: f32,
    pub allows_stealing: bool,
    pub generation: u32,
}

impl StartVoiceCommand {
    /// Zero command used to fill the ring storage; never scheduled.
    pub const EMPTY: Self = Self {
        sample: 0,
        gain: 0.0,
        rate: 1.0,
        allows_stealing: false,
        generation: 0,
    };
}

struct VoiceState {
    /// Bank slot handle; `u32::MAX` means the voice is inactive.
    sample: u32,
    position: f64,
    gain: f32,
    rate: f32,
    started_epoch: u64,
}

impl VoiceState {
    const INACTIVE: Self = Self {
        sample: u32::MAX,
        position: 0.0,
        gain: 0.0,
        rate: 1.0,
        started_epoch: 0,
    };

    fn is_active(&self) -> bool {
        self.sample != u32::MAX
    }
}

/// Voice array guarded by the single-RT-thread rule (module docs).
struct VoiceArray(UnsafeCell<[VoiceState; VOICE_COUNT]>);

// The array is only touched by the one realtime render thread; see the
// module docs for the threading model.
unsafe impl Send for VoiceArray {}
unsafe impl Sync for VoiceArray {}

impl VoiceArray {
    fn new() -> Self {
        Self(UnsafeCell::new([VoiceState::INACTIVE; VOICE_COUNT]))
    }
}

pub struct Mixer {
    voices: VoiceArray,
    /// Round-robin cursor: next place to look for a free voice (RT only).
    voice_cursor: UnsafeCell<usize>,
    scheduled_voice_epoch: UnsafeCell<u64>,
    /// Bumped on reset; queued commands with a stale generation are dropped.
    generation: AtomicU32,
    /// Set on reset, consumed by the next render.
    reset_requested: AtomicBool,
    /// Frames of continuous silence rendered (RT only).
    idle_output_frame_count: UnsafeCell<u32>,
}

impl Default for Mixer {
    fn default() -> Self {
        Self::new()
    }
}

impl Mixer {
    pub fn new() -> Self {
        Self {
            voices: VoiceArray::new(),
            voice_cursor: UnsafeCell::new(0),
            scheduled_voice_epoch: UnsafeCell::new(0),
            generation: AtomicU32::new(0),
            reset_requested: AtomicBool::new(false),
            idle_output_frame_count: UnsafeCell::new(0),
        }
    }

    /// Validates and queues a start command (control thread or FFI call).
    /// Mirrors `PolyphonicSampleProvider.TrySchedule` exactly: reject
    /// non-finite values, clamp gain and rate, cap the queue. Takes a bank
    /// reference for the queued command and returns it if the push fails.
    // The signature mirrors the C ABI schedule command; grouping the
    // channels into a struct would obscure the call sites.
    #[allow(clippy::too_many_arguments)]
    pub fn try_schedule(
        &self,
        ring: &SpscCommandRing,
        bank: &SampleBank,
        stats: &AudioStats,
        sample: u32,
        gain: f32,
        rate: f32,
        allows_stealing: bool,
    ) -> bool {
        if !gain.is_finite() || !rate.is_finite() {
            return false;
        }
        let gain = gain.clamp(0.0, 1.0);
        let rate = rate.clamp(MINIMUM_PLAYBACK_RATE, MAXIMUM_PLAYBACK_RATE);
        bank.take_command_reference(sample);
        let queued = ring.push(StartVoiceCommand {
            sample,
            gain,
            rate,
            allows_stealing,
            generation: self.generation.load(Ordering::Acquire),
        });
        if !queued {
            bank.release_command_reference(sample);
            // The ring has the same capacity as the Windows queue, so a full
            // ring is the same condition Windows counts as a dropped command.
            stats
                .dropped_commands
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        }
        queued
    }

    /// Invalidates queued commands and clears voices on the next render
    /// (output restart, mirrors `ResetForOutputRestart`).
    pub fn reset_for_output_restart(&self) {
        self.generation.fetch_add(1, Ordering::AcqRel);
        self.reset_requested.store(true, Ordering::Release);
    }

    /// Renders the next `output.len() / OUTPUT_CHANNELS` frames into the
    /// interleaved stereo buffer. Realtime-safe: no allocation, no lock.
    /// Returns whether the buffer was continuously silent (for idle stats).
    ///
    /// # Safety
    /// Must only ever be called from the single realtime render thread.
    pub unsafe fn render(
        &self,
        ring: &SpscCommandRing,
        bank: &SampleBank,
        stats: &AudioStats,
        output: &mut [f32],
    ) -> bool {
        // SAFETY: single-RT-thread rule (see module docs).
        let voices = unsafe { &mut *self.voices.0.get() };
        // SAFETY: RT-thread-only cursor state.
        let voice_cursor = unsafe { &mut *self.voice_cursor.get() };
        // SAFETY: RT-thread-only epoch counter.
        let scheduled_voice_epoch = unsafe { &mut *self.scheduled_voice_epoch.get() };
        // SAFETY: RT-thread-only idle counter.
        let idle_output_frame_count = unsafe { &mut *self.idle_output_frame_count.get() };

        if self.reset_requested.swap(false, Ordering::AcqRel) {
            for voice in voices.iter_mut() {
                if voice.is_active() {
                    bank.release_voice_reference(voice.sample);
                }
                *voice = VoiceState::INACTIVE;
            }
            *voice_cursor = 0;
            *scheduled_voice_epoch = 0;
            *idle_output_frame_count = 0;
        }

        let current_generation = self.generation.load(Ordering::Acquire);
        let mut drained = 0;
        while drained < crate::MAX_QUEUED_COMMANDS {
            let Some(command) = ring.pop() else {
                break;
            };
            drained += 1;
            if command.generation != current_generation {
                bank.release_command_reference(command.sample);
                continue;
            }

            // First inactive voice scanning round-robin from the cursor; if
            // all are busy and stealing is allowed, the oldest active voice
            // loses its slot. Pointer voices (stealing denied) are dropped —
            // they can never take a keyboard voice (plan §6.1).
            let mut selected = None;
            for offset in 0..VOICE_COUNT {
                let index = (*voice_cursor + offset) % VOICE_COUNT;
                if !voices[index].is_active() {
                    selected = Some(index);
                    break;
                }
            }
            let selected = selected.or_else(|| {
                if !command.allows_stealing {
                    return None;
                }
                let (oldest_index, _) =
                    (1..VOICE_COUNT).fold((0usize, voices[0].started_epoch), |acc, index| {
                        if voices[index].started_epoch < acc.1 {
                            (index, voices[index].started_epoch)
                        } else {
                            acc
                        }
                    });
                Some(oldest_index)
            });
            let Some(selected) = selected else {
                bank.release_command_reference(command.sample);
                stats
                    .dropped_commands
                    .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                continue;
            };

            if voices[selected].is_active() {
                bank.release_voice_reference(voices[selected].sample);
                stats
                    .voice_steals
                    .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            }

            *scheduled_voice_epoch += 1;
            voices[selected] = VoiceState {
                sample: command.sample,
                position: 0.0,
                gain: command.gain,
                rate: command.rate,
                started_epoch: *scheduled_voice_epoch,
            };
            *voice_cursor = (selected + 1) % VOICE_COUNT;
        }

        let output_frames = output.len() / OUTPUT_CHANNELS;
        debug_assert_eq!(output_frames * OUTPUT_CHANNELS, output.len());
        for frame in output.iter_mut() {
            *frame = 0.0;
        }

        let mut rendered_voice = false;
        for voice in voices.iter_mut() {
            if !voice.is_active() {
                continue;
            }

            rendered_voice = true;
            let samples = bank.samples_for(voice.sample);
            let mut position = voice.position;
            let mut output_frame = 0;
            while output_frame < output_frames && (position as usize) < samples.len() {
                let value = cubic_interpolate(samples, position) * voice.gain;
                let output_index = output_frame * OUTPUT_CHANNELS;
                output[output_index] += value;
                output[output_index + 1] += value;
                position += voice.rate as f64;
                output_frame += 1;
            }

            voice.position = position;
            if position as usize >= samples.len() {
                bank.release_voice_reference(voice.sample);
                *voice = VoiceState::INACTIVE;
            }
        }

        if !rendered_voice && ring.is_empty() {
            *idle_output_frame_count = crate::IDLE_OUTPUT_FRAME_COUNT
                .min(idle_output_frame_count.saturating_add(output_frames as u32));
        } else {
            *idle_output_frame_count = 0;
        }
        let idle = *idle_output_frame_count >= crate::IDLE_OUTPUT_FRAME_COUNT;

        for frame in output.iter_mut() {
            *frame = frame.clamp(-1.0, 1.0);
        }
        idle
    }
}

/// Catmull-Rom four-point interpolation, index-clamped at both ends — the
/// exact arithmetic of `PolyphonicSampleProvider.CubicInterpolate`.
pub fn cubic_interpolate(samples: &[f32], position: f64) -> f32 {
    let index1 = position as usize;
    if index1 >= samples.len().saturating_sub(1) {
        return samples[samples.len() - 1];
    }

    let index0 = index1.saturating_sub(1);
    let index2 = index1 + 1;
    let index3 = (index1 + 2).min(samples.len() - 1);
    let fraction = (position - index1 as f64) as f32;

    let p0 = samples[index0];
    let p1 = samples[index1];
    let p2 = samples[index2];
    let p3 = samples[index3];
    let fraction_squared = fraction * fraction;
    let fraction_cubed = fraction_squared * fraction;

    0.5 * ((2.0 * p1)
        + ((-p0 + p2) * fraction)
        + ((2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * fraction_squared)
        + ((-p0 + 3.0 * p1 - 3.0 * p2 + p3) * fraction_cubed))
}
