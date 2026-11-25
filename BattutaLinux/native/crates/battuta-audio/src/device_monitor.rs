//! Default-output-device monitor (plan §6.1 "notify default device changes",
//! §6.3 "only rebuild the stream after default output device changes …").
//!
//! A second, registry-only PipeWire connection joins the same daemon and
//! binds the WirePlumber `default` metadata. Every change to a
//! `default.audio.sink*` property raises `EVENT_DEVICE_CHANGED` on the host
//! event callback, which the C# runtime maps to a stream rebuild. The initial
//! metadata dump at bind time is recorded as a baseline and does not emit.
//!
//! The monitor owns its own main loop thread and never touches the stream,
//! the mixer, or the engine banks. All FFI is hand-declared like the rest of
//! the shim (no `-sys` crates): registry events come from exported libpipewire
//! symbols, while metadata event subscription has no exported symbol and is
//! performed through the object's `spa_callbacks` vtable exactly as the C
//! `pw_metadata_add_listener` static inline does.

use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::sync::Arc;
use std::thread::JoinHandle;

use crate::engine::AudioEngine;

/// Raised when the default output device (or its configuration route)
/// changes. Not the realtime thread — fires on the monitor main loop.
pub const EVENT_DEVICE_CHANGED: i32 = 3;

const METADATA_INTERFACE: &str = "PipeWire:Interface:Metadata";
const PW_VERSION_REGISTRY: u32 = 3;
const PW_VERSION_REGISTRY_EVENTS: u32 = 0;
const PW_VERSION_METADATA: u32 = 3;
const PW_VERSION_METADATA_EVENTS: u32 = 0;
const METADATA_NAME_KEY: &str = "metadata.name";
const METADATA_NAME: &str = "default";
/// Every key that affects the default output route (`default.audio.sink`,
/// `default.config.audio.sink`, …).
const DEFAULT_SINK_PREFIX: &str = "default.audio.sink";

// ---------------------------------------------------------------------------
// Raw FFI declarations and struct layouts (verified against the installed
// pipewire 1.6.8 headers; see the PR 7 devlog).
// ---------------------------------------------------------------------------

/// `struct spa_hook` — five pointers; the entire hook is zero-initialized.
#[repr(C)]
struct SpaHook {
    link: [*mut c_void; 2],
    cb_funcs: *const c_void,
    cb_data: *mut c_void,
    removed: *const c_void,
    priv_: *mut c_void,
}

impl SpaHook {
    fn zeroed() -> Self {
        // SAFETY: SpaHook is plain old data; an all-zero hook is what C code
        // produces with `spa_zero(hook)` before add_listener.
        unsafe { std::mem::zeroed() }
    }
}

/// `struct spa_dict` / `struct spa_dict_item` (spa/utils/dict.h).
#[repr(C)]
struct SpaDict {
    flags: u32,
    n_items: u32,
    items: *const SpaDictItem,
}

#[repr(C)]
struct SpaDictItem {
    key: *const c_char,
    value: *const c_char,
}

/// `struct pw_registry_events` (PW_VERSION_REGISTRY_EVENTS = 0): version,
/// `global`, `global_remove` — no other members (core.h).
#[repr(C)]
struct PwRegistryEvents {
    version: u32,
    global: Option<unsafe extern "C" fn(*mut c_void, u32, u32, *const c_char, u32, *const SpaDict)>,
    global_remove: Option<unsafe extern "C" fn(*mut c_void, u32)>,
}

/// `struct pw_metadata_events` (PW_VERSION_METADATA_EVENTS = 0): version and
/// the `property` callback only.
#[repr(C)]
struct PwMetadataEvents {
    version: u32,
    property: Option<
        unsafe extern "C" fn(
            *mut c_void,
            u32,
            *const c_char,
            *const c_char,
            *const c_char,
        ) -> c_int,
    >,
}

/// `struct pw_metadata_methods` — the vtable behind a bound metadata proxy.
#[repr(C)]
struct PwMetadataMethods {
    version: u32,
    add_listener: Option<
        unsafe extern "C" fn(
            *mut c_void,
            *mut SpaHook,
            *const PwMetadataEvents,
            *mut c_void,
        ) -> c_int,
    >,
    set_property: Option<unsafe extern "C" fn()>,
    clear: Option<unsafe extern "C" fn()>,
}

/// `struct spa_callbacks` (spa/utils/hook.h).
#[repr(C)]
struct SpaCallbacks {
    funcs: *const c_void,
    data: *mut c_void,
}

/// `struct spa_interface` (spa/utils/hook.h).
#[repr(C)]
struct SpaInterface {
    type_: *const c_char,
    version: u32,
    cb: SpaCallbacks,
}

/// `struct pw_metadata` — just an `spa_interface` header.
#[repr(C)]
struct PwMetadata {
    iface: SpaInterface,
}

unsafe extern "C" {
    fn pw_main_loop_new(props: *const c_void) -> *mut c_void;
    fn pw_main_loop_get_loop(loop_: *mut c_void) -> *mut c_void;
    fn pw_main_loop_run(loop_: *mut c_void) -> c_int;
    fn pw_main_loop_quit(loop_: *mut c_void);
    fn pw_main_loop_destroy(loop_: *mut c_void);
    // pw_context_new takes the inner `struct pw_loop *` of the main loop,
    // not the main loop itself (context.h).
    fn pw_context_new(
        main_loop: *mut c_void,
        props: *const c_void,
        user_data_size: usize,
    ) -> *mut c_void;
    fn pw_context_destroy(context: *mut c_void);
    fn pw_context_connect(
        context: *mut c_void,
        props: *const c_void,
        user_data_size: usize,
    ) -> *mut c_void;
    fn pw_core_disconnect(core: *mut c_void) -> c_int;
    fn pw_core_get_registry(core: *mut c_void, version: u32, user_data_size: usize) -> *mut c_void;
    fn pw_registry_add_listener(
        registry: *mut c_void,
        listener: *mut SpaHook,
        events: *const PwRegistryEvents,
        data: *mut c_void,
    ) -> c_int;
    fn pw_registry_bind(
        registry: *mut c_void,
        id: u32,
        type_: *const c_char,
        version: u32,
        user_data_size: usize,
    ) -> *mut c_void;
}

// ---------------------------------------------------------------------------
// Monitor state.
// ---------------------------------------------------------------------------

/// Raw pointer wrapper so the loop entry closure can cross `thread::spawn`
/// (`*mut c_void` alone is `!Send`). The pointee outlives the closure: the
/// loop is only destroyed after the thread has been joined.
#[derive(Clone, Copy)]
struct SendPtr(*mut c_void);
unsafe impl Send for SendPtr {}

/// Entry point for the monitor thread; a named function so the spawn closure
/// captures the `SendPtr` wrapper (field-precise closure capture would
/// otherwise capture the raw pointer field directly).
fn run_monitor_loop(loop_: SendPtr) {
    // SAFETY: the loop is valid until `stop` joins this thread.
    unsafe { pw_main_loop_run(loop_.0) };
}

/// Shared with the loop-thread callbacks. Only the monitor main-loop thread
/// touches the mutable fields, and teardown happens strictly after the loop
/// thread has been joined.
struct MonitorInner {
    engine: Arc<AudioEngine>,
    main_loop: *mut c_void,
    context: *mut c_void,
    core: *mut c_void,
    registry: *mut c_void,
    registry_events: *const PwRegistryEvents,
    /// Stable hook address required by `pw_registry_add_listener`.
    #[allow(dead_code)] // read by libpipewire, not by Rust code
    registry_hook: Box<SpaHook>,
    metadata_events: *const PwMetadataEvents,
    #[allow(dead_code)]
    metadata_hook: Box<SpaHook>,
    /// Global id of the bound `default` metadata (0 = not bound).
    default_metadata_id: u32,
    /// Baseline of already-seen `default.audio.sink*` values; the initial
    /// dump must not emit.
    seen_defaults: HashMap<String, String>,
}

impl MonitorInner {
    /// Binds the `default` metadata global and subscribes to its property
    /// events. Runs on the monitor loop thread (from the `global` callback).
    fn bind_default_metadata(&mut self, id: u32) {
        if self.default_metadata_id != 0 {
            return; // Already bound.
        }
        let type_name = CString::new(METADATA_INTERFACE).expect("constant has no NUL");
        // Reset the hook in place: `add_listener` registered this exact
        // address with the proxy, so the Box itself must never be replaced.
        *self.metadata_hook = SpaHook::zeroed();
        // SAFETY: registry is a live proxy; the type string outlives the call.
        let metadata = unsafe {
            pw_registry_bind(
                self.registry,
                id,
                type_name.as_ptr(),
                PW_VERSION_METADATA,
                0,
            )
        };
        if metadata.is_null() {
            crate::log_warn("device monitor: binding the default metadata failed");
            return;
        }

        // `pw_metadata_add_listener` has no exported symbol: replicate the C
        // static inline by invoking the object's method vtable
        // (`spa_callbacks_call(cb, pw_metadata_methods, add_listener, …)`).
        // SAFETY: the bound object starts with an `spa_interface` whose
        // callbacks point at libpipewire's metadata proxy implementation.
        let added = unsafe {
            let metadata = &*metadata.cast::<PwMetadata>();
            let methods = metadata.iface.cb.funcs.cast::<PwMetadataMethods>();
            if methods.is_null() {
                0
            } else {
                let add_listener = (*methods).add_listener;
                match add_listener {
                    Some(add_listener) => add_listener(
                        metadata.iface.cb.data,
                        &mut *self.metadata_hook,
                        self.metadata_events,
                        self as *mut MonitorInner as *mut c_void,
                    ),
                    None => 0,
                }
            }
        };
        if added == 0 {
            crate::log_warn("device monitor: metadata add_listener failed");
            return;
        }

        self.default_metadata_id = id;
        self.seen_defaults.clear();
        crate::log_warn("device monitor: bound the default metadata");
    }

    /// Forgets a removed metadata binding so a recreated global re-binds.
    fn unbind_default_metadata(&mut self, id: u32) {
        if self.default_metadata_id == id {
            self.default_metadata_id = 0;
            self.seen_defaults.clear();
        }
    }
}

unsafe extern "C" fn on_registry_global(
    data: *mut c_void,
    id: u32,
    _permissions: u32,
    type_: *const c_char,
    _version: u32,
    props: *const SpaDict,
) {
    if data.is_null() || type_.is_null() || props.is_null() {
        return;
    }
    // SAFETY: `data` points at the MonitorInner box owned by DeviceMonitor;
    // the type string and property dict are valid for the callback duration.
    unsafe {
        let type_name = CStr::from_ptr(type_);
        if type_name.to_bytes() != METADATA_INTERFACE.as_bytes() {
            return;
        }
        let dict = &*props;
        let items = std::slice::from_raw_parts(dict.items, dict.n_items as usize);
        let mut name_matches = false;
        for item in items {
            if item.key.is_null() || item.value.is_null() {
                continue;
            }
            if CStr::from_ptr(item.key).to_bytes() == METADATA_NAME_KEY.as_bytes()
                && CStr::from_ptr(item.value).to_bytes() == METADATA_NAME.as_bytes()
            {
                name_matches = true;
                break;
            }
        }
        if name_matches {
            let inner = &mut *(data as *mut MonitorInner);
            inner.bind_default_metadata(id);
        }
    }
}

unsafe extern "C" fn on_registry_global_remove(data: *mut c_void, id: u32) {
    if data.is_null() {
        return;
    }
    // SAFETY: `data` points at the MonitorInner box owned by DeviceMonitor.
    unsafe {
        let inner = &mut *(data as *mut MonitorInner);
        inner.unbind_default_metadata(id);
    }
}

unsafe extern "C" fn on_metadata_property(
    data: *mut c_void,
    _subject: u32,
    key: *const c_char,
    _type: *const c_char,
    value: *const c_char,
) -> c_int {
    if data.is_null() || key.is_null() {
        return 0;
    }
    // SAFETY: `data` points at the MonitorInner box owned by DeviceMonitor;
    // key/value strings are valid for the callback duration.
    unsafe {
        let key = CStr::from_ptr(key);
        if !key.to_bytes().starts_with(DEFAULT_SINK_PREFIX.as_bytes()) {
            return 0;
        }
        let value = if value.is_null() {
            String::new()
        } else {
            CStr::from_ptr(value).to_string_lossy().into_owned()
        };
        let inner = &mut *(data as *mut MonitorInner);
        let key = key.to_string_lossy().into_owned();
        let changed = match inner.seen_defaults.get(&key) {
            Some(previous) => previous != &value,
            None => false, // initial dump: record the baseline silently
        };
        inner.seen_defaults.insert(key, value.clone());
        if changed {
            crate::log_warn(&format!("default output device changed: {value}"));
            if let Some(event) = inner.engine.event_callback() {
                event(EVENT_DEVICE_CHANGED, inner.engine.event_user_data());
            }
        }
    }
    0
}

// ---------------------------------------------------------------------------
// Public wrapper.
// ---------------------------------------------------------------------------

/// Owns the monitor connection and its main-loop thread.
pub struct DeviceMonitor {
    inner: Option<Box<MonitorInner>>,
    thread: Option<JoinHandle<()>>,
}

impl DeviceMonitor {
    /// Connects a registry-only context to the default PipeWire daemon and
    /// starts watching the default metadata on a dedicated thread.
    pub fn start(engine: Arc<AudioEngine>) -> Result<Self, String> {
        crate::pipewire::pipewire_init();

        // SAFETY: no properties; every object created here is destroyed in
        // `stop` exactly once, in reverse creation order.
        let main_loop = unsafe { pw_main_loop_new(std::ptr::null()) };
        if main_loop.is_null() {
            return Err("pw_main_loop_new failed for the device monitor".to_owned());
        }
        // SAFETY: main_loop is valid; the context is destroyed in `stop`.
        // `pw_context_new` needs the inner `pw_loop`, not the main loop.
        let loop_ = unsafe { pw_main_loop_get_loop(main_loop) };
        let context = unsafe { pw_context_new(loop_, std::ptr::null(), 0) };
        if context.is_null() {
            // SAFETY: main_loop is valid and owned by this function.
            unsafe { pw_main_loop_destroy(main_loop) };
            return Err("pw_context_new failed for the device monitor".to_owned());
        }
        // SAFETY: context is valid; the core is disconnected in `stop`.
        let core = unsafe { pw_context_connect(context, std::ptr::null(), 0) };
        if core.is_null() {
            // SAFETY: context and main_loop are valid and owned here.
            unsafe {
                pw_context_destroy(context);
                pw_main_loop_destroy(main_loop);
            }
            return Err("device monitor could not connect to PipeWire".to_owned());
        }
        // SAFETY: core is valid; the registry is freed by the disconnect.
        let registry = unsafe { pw_core_get_registry(core, PW_VERSION_REGISTRY, 0) };
        if registry.is_null() {
            // SAFETY: core, context, and main_loop are valid and owned here.
            unsafe {
                pw_core_disconnect(core);
                pw_context_destroy(context);
                pw_main_loop_destroy(main_loop);
            }
            return Err("pw_core_get_registry failed for the device monitor".to_owned());
        }

        let registry_events = Box::into_raw(Box::new(PwRegistryEvents {
            version: PW_VERSION_REGISTRY_EVENTS,
            global: Some(on_registry_global),
            global_remove: Some(on_registry_global_remove),
        }));
        let metadata_events = Box::into_raw(Box::new(PwMetadataEvents {
            version: PW_VERSION_METADATA_EVENTS,
            property: Some(on_metadata_property),
        }));
        let mut inner = Box::new(MonitorInner {
            engine,
            main_loop,
            context,
            core,
            registry,
            registry_events,
            registry_hook: Box::new(SpaHook::zeroed()),
            metadata_events,
            metadata_hook: Box::new(SpaHook::zeroed()),
            default_metadata_id: 0,
            seen_defaults: HashMap::new(),
        });

        // SAFETY: registry and hook outlive the listener (freed in `stop`
        // after the loop thread has joined and the core disconnected).
        let added = unsafe {
            pw_registry_add_listener(
                registry,
                &mut *inner.registry_hook,
                registry_events,
                inner.as_mut() as *mut MonitorInner as *mut c_void,
            )
        };
        if added != 0 {
            let error = format!("pw_registry_add_listener failed: {added}");
            let mut monitor = DeviceMonitor {
                inner: Some(inner),
                thread: None,
            };
            monitor.stop();
            return Err(error);
        }

        // SAFETY: main_loop is valid; `run` blocks until `quit` from `stop`.
        let loop_ptr = SendPtr(main_loop);
        let spawned = std::thread::Builder::new()
            .name("battuta-device-monitor".to_owned())
            .spawn(move || run_monitor_loop(loop_ptr));
        let thread = match spawned {
            Ok(thread) => thread,
            Err(error) => {
                let mut monitor = DeviceMonitor {
                    inner: Some(inner),
                    thread: None,
                };
                monitor.stop();
                return Err(format!(
                    "spawning the device monitor thread failed: {error}"
                ));
            }
        };

        Ok(DeviceMonitor {
            inner: Some(inner),
            thread: Some(thread),
        })
    }

    /// Quits the loop, joins the thread, and tears the FFI objects down.
    fn stop(&mut self) {
        let Some(mut inner) = self.inner.take() else {
            return;
        };
        if let Some(thread) = self.thread.take() {
            // SAFETY: main_loop is valid; quit is thread-safe and makes the
            // `pw_main_loop_run` call on the monitor thread return.
            unsafe { pw_main_loop_quit(inner.main_loop) };
            let _ = thread.join();
        }
        // SAFETY: the loop thread is gone; the core and its proxies (registry
        // and any bound metadata) are destroyed by the disconnect, then the
        // context and main loop in reverse creation order.
        unsafe {
            if !inner.core.is_null() {
                pw_core_disconnect(inner.core);
                inner.core = std::ptr::null_mut();
            }
            if !inner.context.is_null() {
                pw_context_destroy(inner.context);
                inner.context = std::ptr::null_mut();
            }
            if !inner.main_loop.is_null() {
                pw_main_loop_destroy(inner.main_loop);
                inner.main_loop = std::ptr::null_mut();
            }
        }
        // Free the event vtables now that no listener references them.
        // SAFETY: both were created with `Box::into_raw` and are no longer
        // referenced after the core disconnect.
        unsafe {
            if !inner.registry_events.is_null() {
                drop(Box::from_raw(inner.registry_events.cast_mut()));
                inner.registry_events = std::ptr::null();
            }
            if !inner.metadata_events.is_null() {
                drop(Box::from_raw(inner.metadata_events.cast_mut()));
                inner.metadata_events = std::ptr::null();
            }
        }
        drop(inner);
    }
}

impl Drop for DeviceMonitor {
    fn drop(&mut self) {
        self.stop();
    }
}
