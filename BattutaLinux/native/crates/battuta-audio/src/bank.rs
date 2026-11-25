//! Registered sample storage shared by the control thread (register,
//! release) and the realtime thread (voice start/end).
//!
//! Memory-safety model without locks and without RT-side allocation or
//! deallocation:
//!
//! - A fixed slot table; the control thread owns slot allocation.
//! - Every live reference (queued command in the ring, active voice) holds
//!   one refcount. `Mixer::try_schedule` takes a reference for the queued
//!   command; the render thread transfers it to the voice (or releases it on
//!   drop); a voice ending (or being stolen) releases it.
//! - A retired slot is recycled **only on the control thread** (inside
//!   [`SampleBank::register`]/[`SampleBank::release`], which sweep pending
//!   slots) and only when its refcount is zero. The realtime thread never
//!   frees memory: when the final reference drops on the render thread the
//!   slot is merely flagged for sweeping.
//! - Reference handoff uses the standard refcount pattern (release store on
//!   decrement, acquire load before free).

use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};

/// Upper bound on registered samples (far above the ~250 bank assets plus
/// headroom for custom packs).
pub const MAX_SAMPLES: usize = 1024;
/// Total PCM bytes across all live samples; mirrors the Windows decode guard
/// (`MaximumDecodedPcmBytes`).
pub const MAX_TOTAL_SAMPLE_BYTES: usize = 64 * 1024 * 1024;

pub struct SampleBank {
    slots: [Slot; MAX_SAMPLES],
    total_bytes: AtomicUsize,
}

struct Slot {
    /// Heap storage; owned by the bank, read by the realtime thread while a
    /// refcount is held.
    data: std::cell::UnsafeCell<Box<[f32]>>,
    /// Slot holds a registered sample.
    present: AtomicBool,
    /// Slot was released; recycle when refcount hits zero and the control
    /// thread sweeps.
    retired: AtomicBool,
    /// Live references (queued command or active voice).
    references: AtomicI64,
}

// The refcount protocol (not a lock) makes concurrent control-thread recycle
// and realtime-thread reads safe; see the module docs.
unsafe impl Send for SampleBank {}
unsafe impl Sync for SampleBank {}

impl SampleBank {
    pub fn new() -> Self {
        Self {
            slots: std::array::from_fn(|_| Slot {
                data: std::cell::UnsafeCell::new(Box::new([])),
                present: AtomicBool::new(false),
                retired: AtomicBool::new(false),
                references: AtomicI64::new(0),
            }),
            total_bytes: AtomicUsize::new(0),
        }
    }

    /// Registers a mono 48 kHz float sample and returns its handle.
    /// Fails when the table or the byte budget is exhausted.
    pub fn register(&self, samples: Vec<f32>) -> Option<u32> {
        if samples.is_empty() {
            return None;
        }
        let bytes = samples.len() * std::mem::size_of::<f32>();
        let mut current = self.total_bytes.load(Ordering::Relaxed);
        loop {
            if current + bytes > MAX_TOTAL_SAMPLE_BYTES {
                return None;
            }
            match self.total_bytes.compare_exchange_weak(
                current,
                current + bytes,
                Ordering::Relaxed,
                Ordering::Relaxed,
            ) {
                Ok(_) => break,
                Err(observed) => current = observed,
            }
        }

        self.sweep();
        for (index, slot) in self.slots.iter().enumerate() {
            if slot.present.load(Ordering::Acquire) {
                continue;
            }
            // Slot is free (never used, or swept). Registration runs on the
            // control thread only; the realtime thread cannot reach it before
            // `present` is set.
            // SAFETY: control thread only.
            let data = unsafe { &mut *slot.data.get() };
            *data = samples.into_boxed_slice();
            slot.references.store(0, Ordering::Relaxed);
            slot.retired.store(false, Ordering::Release);
            slot.present.store(true, Ordering::Release);
            return Some(index as u32);
        }
        // Table full: roll the byte reservation back.
        self.total_bytes.fetch_sub(bytes, Ordering::Relaxed);
        None
    }

    /// Releases a handle; the slot recycles lazily once no reference remains.
    pub fn release(&self, handle: u32) {
        let Some(slot) = self.slot(handle) else {
            return;
        };
        slot.retired.store(true, Ordering::Release);
        self.sweep();
    }

    /// Control thread: takes the reference a queued command will hold.
    /// Called by `Mixer::try_schedule` before the ring push; on push failure
    /// the reference is returned via [`Self::release_command_reference`].
    pub fn take_command_reference(&self, handle: u32) {
        if let Some(slot) = self.slot(handle) {
            slot.references.fetch_add(1, Ordering::AcqRel);
        }
    }

    /// Realtime path: a queued command was dropped (stale generation, full
    /// ring, no voice available).
    pub fn release_command_reference(&self, handle: u32) {
        self.drop_reference(handle);
    }

    /// Realtime path: a voice ended or was stolen.
    pub fn release_voice_reference(&self, handle: u32) {
        self.drop_reference(handle);
    }

    /// Realtime path: sample data for a handle with a held reference.
    pub fn samples_for(&self, handle: u32) -> &[f32] {
        match self.slot(handle) {
            // SAFETY: a reference is held for every handle the render thread
            // sees (see module docs), so the control thread cannot recycle
            // the slot while this slice is alive.
            Some(slot) => unsafe { &*slot.data.get() }.as_ref(),
            None => &[],
        }
    }

    fn drop_reference(&self, handle: u32) {
        if let Some(slot) = self.slot(handle) {
            // Release ordering publishes everything before the decrement; the
            // sweeper observes the zero with acquire. Never frees here: the
            // final decrement can run on the realtime thread.
            slot.references.fetch_sub(1, Ordering::Release);
        }
    }

    /// Control thread: frees every retired slot whose references are gone.
    fn sweep(&self) {
        for slot in &self.slots {
            if !slot.retired.load(Ordering::Acquire)
                || slot.references.load(Ordering::Acquire) != 0
                || !slot.present.load(Ordering::Acquire)
            {
                continue;
            }
            // present is only cleared here, on the control thread, under
            // retired + zero-references, so no thread can be reading data.
            slot.present.store(false, Ordering::Release);
            // SAFETY: no reference exists (retired + zero references), so
            // nobody can hold a slice into this storage.
            let data = unsafe { &mut *slot.data.get() };
            let freed = data.len() * std::mem::size_of::<f32>();
            *data = Box::new([]);
            self.total_bytes.fetch_sub(freed, Ordering::Relaxed);
            // Make the slot acquirable by `register` again.
            slot.retired.store(false, Ordering::Release);
        }
    }

    fn slot(&self, handle: u32) -> Option<&Slot> {
        let handle = handle as usize;
        if handle >= MAX_SAMPLES {
            return None;
        }
        let slot = &self.slots[handle];
        if slot.present.load(Ordering::Acquire) {
            Some(slot)
        } else {
            None
        }
    }
}

impl Default for SampleBank {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn register_release_round_trip() {
        let bank = SampleBank::new();
        let handle = bank.register(vec![0.0; 128]).expect("handle");
        assert_eq!(bank.samples_for(handle).len(), 128);
        bank.release(handle);
        // After release the slot is recycled lazily; reads return empty.
        assert_eq!(bank.samples_for(handle).len(), 0);
    }

    #[test]
    fn register_reuses_recycled_slots() {
        let bank = SampleBank::new();
        let first = bank.register(vec![0.0; 16]).expect("first");
        bank.release(first);
        let second = bank.register(vec![0.0; 16]).expect("second");
        assert_eq!(first, second, "freed slot should be reused");
    }

    #[test]
    fn reference_keeps_data_readable_until_dropped_and_swept() {
        let bank = SampleBank::new();
        let handle = bank.register(vec![0.5; 8]).expect("handle");
        bank.take_command_reference(handle);
        bank.release(handle);
        // Still readable because a reference is held.
        assert_eq!(bank.samples_for(handle)[0], 0.5);
        bank.release_command_reference(handle);
        // The last reference dropped; the slot is only flagged, never freed
        // on the dropping thread, so the data stays readable.
        assert_eq!(bank.samples_for(handle).len(), 8);
        // The next control-thread register sweeps and reuses the slot.
        let recycled = bank.register(vec![0.0; 4]).expect("sweep trigger");
        assert_eq!(recycled, handle, "swept slot should be reused");
        assert_eq!(bank.samples_for(handle).len(), 4);
    }

    #[test]
    fn byte_budget_is_enforced() {
        let bank = SampleBank::new();
        // One byte over half the budget so two registrations cannot fit.
        let frames = MAX_TOTAL_SAMPLE_BYTES / std::mem::size_of::<f32>() / 2 + 1;
        let first = bank.register(vec![0.0; frames]).expect("first half");
        assert!(
            bank.register(vec![0.0; frames]).is_none(),
            "second half must fail"
        );
        bank.release(first);
        let after = bank
            .register(vec![0.0; frames])
            .expect("budget rolled back");
        assert_eq!(after, first);
    }
}
