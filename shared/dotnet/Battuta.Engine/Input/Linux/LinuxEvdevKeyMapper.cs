using Battuta.Core.Input;

namespace Battuta.Engine.Input.Linux;

/// <summary>
/// Maps Linux evdev <c>KEY_*</c> codes to Battuta's layout-independent physical IDs.
/// Pure and static: evdev codes are plain u16 numbers, so there is no P/Invoke or
/// Linux runtime dependency and the table behaves identically on every platform.
/// Code numbers mirror the kernel uapi header
/// <c>/usr/include/linux/input-event-codes.h</c> (checked against 6.x).
/// </summary>
/// <remarks>
/// Pointer <c>BTN_*</c> codes are a separate namespace (handled by the broker event
/// adapter) and never appear in this table. Media keys beyond the volume trio are
/// intentionally unmapped: the catalog has no stable IDs for them yet, and unmapped
/// codes keep flowing under <see cref="GetUnknownKeyId"/> per the broker contract.
/// </remarks>
public static class LinuxEvdevKeyMapper
{
    /// <summary>Namespace prefix of the stable ID minted for unmapped codes.</summary>
    public const string UnknownKeyIdPrefix = "linux.evdev.";

    private static readonly Dictionary<ushort, PhysicalKeyId> KeyboardCodes = BuildTable();

    /// <summary>
    /// Maps an evdev <c>KEY_*</c> code to its known physical ID. Unmapped codes must
    /// fall back to <see cref="GetUnknownKeyId"/> (never an error): the broker contract
    /// requires unknown keys to keep flowing under a stable neutral ID.
    /// </summary>
    public static bool TryGetKeyId(ushort code, out PhysicalKeyId keyId) =>
        KeyboardCodes.TryGetValue(code, out keyId);

    /// <summary>
    /// Returns the modifier flag for one of the eight left/right modifier codes
    /// (shift, control, alt, meta); false for every other code.
    /// </summary>
    public static bool TryGetModifier(ushort code, out ModifierState modifier)
    {
        modifier = code switch
        {
            42 => ModifierState.LeftShift, // KEY_LEFTSHIFT
            54 => ModifierState.RightShift, // KEY_RIGHTSHIFT
            29 => ModifierState.LeftControl, // KEY_LEFTCTRL
            97 => ModifierState.RightControl, // KEY_RIGHTCTRL
            56 => ModifierState.LeftAlt, // KEY_LEFTALT
            100 => ModifierState.RightAlt, // KEY_RIGHTALT
            125 => ModifierState.LeftMeta, // KEY_LEFTMETA
            126 => ModifierState.RightMeta, // KEY_RIGHTMETA
            _ => ModifierState.None,
        };

        return modifier != ModifierState.None;
    }

    /// <summary>
    /// True for the mapped <c>KEY_*</c> codes the broker passes through as keyboard
    /// events. Deliberately conservative: codes that are physical keys but lack a
    /// catalog ID (media/AC keys) are reported false and are delivered under
    /// <see cref="GetUnknownKeyId"/> instead.
    /// </summary>
    public static bool IsKeyboardKeyCode(ushort code) => KeyboardCodes.ContainsKey(code);

    /// <summary>
    /// Deterministic neutral ID for an unmapped evdev code, e.g. <c>linux.evdev.248</c>.
    /// Distinct codes always yield distinct IDs so statistics never collapse keys.
    /// </summary>
    public static PhysicalKeyId GetUnknownKeyId(ushort code) => new($"{UnknownKeyIdPrefix}{(ushort)code}");

    private static Dictionary<ushort, PhysicalKeyId> BuildTable()
    {
        var result = new Dictionary<ushort, PhysicalKeyId>(capacity: 128);

        // Main alphanumeric block.
        Add(result, 1, PhysicalKeys.Escape);
        AddRange(result, 2, [
            PhysicalKeys.Digit1, PhysicalKeys.Digit2, PhysicalKeys.Digit3,
            PhysicalKeys.Digit4, PhysicalKeys.Digit5, PhysicalKeys.Digit6,
            PhysicalKeys.Digit7, PhysicalKeys.Digit8, PhysicalKeys.Digit9,
            PhysicalKeys.Digit0]);
        Add(result, 12, PhysicalKeys.Minus);
        Add(result, 13, PhysicalKeys.Equal);
        Add(result, 14, PhysicalKeys.Backspace);
        Add(result, 15, PhysicalKeys.Tab);
        AddRange(result, 16, [
            PhysicalKeys.KeyQ, PhysicalKeys.KeyW, PhysicalKeys.KeyE,
            PhysicalKeys.KeyR, PhysicalKeys.KeyT, PhysicalKeys.KeyY,
            PhysicalKeys.KeyU, PhysicalKeys.KeyI, PhysicalKeys.KeyO,
            PhysicalKeys.KeyP]);
        Add(result, 26, PhysicalKeys.LeftBracket);
        Add(result, 27, PhysicalKeys.RightBracket);
        Add(result, 28, PhysicalKeys.Enter);
        Add(result, 29, PhysicalKeys.LeftControl);
        AddRange(result, 30, [
            PhysicalKeys.KeyA, PhysicalKeys.KeyS, PhysicalKeys.KeyD,
            PhysicalKeys.KeyF, PhysicalKeys.KeyG, PhysicalKeys.KeyH,
            PhysicalKeys.KeyJ, PhysicalKeys.KeyK, PhysicalKeys.KeyL]);
        Add(result, 39, PhysicalKeys.Semicolon);
        Add(result, 40, PhysicalKeys.Quote);
        Add(result, 41, PhysicalKeys.Backquote);
        Add(result, 42, PhysicalKeys.LeftShift);
        Add(result, 43, PhysicalKeys.Backslash);
        AddRange(result, 44, [
            PhysicalKeys.KeyZ, PhysicalKeys.KeyX, PhysicalKeys.KeyC,
            PhysicalKeys.KeyV, PhysicalKeys.KeyB, PhysicalKeys.KeyN,
            PhysicalKeys.KeyM]);
        Add(result, 51, PhysicalKeys.Comma);
        Add(result, 52, PhysicalKeys.Period);
        Add(result, 53, PhysicalKeys.Slash);
        Add(result, 54, PhysicalKeys.RightShift);
        Add(result, 55, PhysicalKeys.NumpadMultiply);
        Add(result, 56, PhysicalKeys.LeftAlt);
        Add(result, 57, PhysicalKeys.Space);
        Add(result, 58, PhysicalKeys.CapsLock);
        AddRange(result, 59, [
            PhysicalKeys.F1, PhysicalKeys.F2, PhysicalKeys.F3, PhysicalKeys.F4,
            PhysicalKeys.F5, PhysicalKeys.F6, PhysicalKeys.F7, PhysicalKeys.F8,
            PhysicalKeys.F9, PhysicalKeys.F10]);
        Add(result, 69, PhysicalKeys.NumLock);
        Add(result, 70, PhysicalKeys.ScrollLock);
        AddRange(result, 71, [
            PhysicalKeys.Numpad7, PhysicalKeys.Numpad8, PhysicalKeys.Numpad9,
            PhysicalKeys.NumpadSubtract, PhysicalKeys.Numpad4, PhysicalKeys.Numpad5,
            PhysicalKeys.Numpad6, PhysicalKeys.NumpadAdd, PhysicalKeys.Numpad1,
            PhysicalKeys.Numpad2, PhysicalKeys.Numpad3, PhysicalKeys.Numpad0,
            PhysicalKeys.NumpadDecimal]);

        // Japanese 106/109-key OADG positions. KEY_102ND is 86; KEY_RIGHTALT is
        // the distinct code 100 (the two are easy to confuse).
        Add(result, 85, PhysicalKeys.ZenkakuHankaku);
        Add(result, 86, PhysicalKeys.IntlBackslash);
        Add(result, 87, PhysicalKeys.F11);
        Add(result, 88, PhysicalKeys.F12);
        Add(result, 89, PhysicalKeys.IntlRo);
        Add(result, 92, PhysicalKeys.Henkan);
        Add(result, 93, PhysicalKeys.Kana);
        Add(result, 94, PhysicalKeys.Muhenkan);
        Add(result, 95, PhysicalKeys.NumpadComma);
        Add(result, 96, PhysicalKeys.NumpadEnter);
        Add(result, 97, PhysicalKeys.RightControl);
        Add(result, 98, PhysicalKeys.NumpadDivide);
        Add(result, 99, PhysicalKeys.PrintScreen); // KEY_SYSRQ
        Add(result, 100, PhysicalKeys.RightAlt);
        Add(result, 117, PhysicalKeys.NumpadEqual);
        Add(result, 119, PhysicalKeys.Pause);
        Add(result, 124, PhysicalKeys.IntlYen);
        Add(result, 125, PhysicalKeys.LeftMeta);
        Add(result, 126, PhysicalKeys.RightMeta);
        Add(result, 127, PhysicalKeys.ContextMenu); // KEY_COMPOSE, the PC "Menu" key

        // Navigation cluster.
        Add(result, 102, PhysicalKeys.Home);
        Add(result, 103, PhysicalKeys.ArrowUp);
        Add(result, 104, PhysicalKeys.PageUp);
        Add(result, 105, PhysicalKeys.ArrowLeft);
        Add(result, 106, PhysicalKeys.ArrowRight);
        Add(result, 107, PhysicalKeys.End);
        Add(result, 108, PhysicalKeys.ArrowDown);
        Add(result, 109, PhysicalKeys.PageDown);
        Add(result, 110, PhysicalKeys.Insert);
        Add(result, 111, PhysicalKeys.Delete);

        // Volume keys: the only media keys with catalog IDs.
        Add(result, 113, PhysicalKeys.AudioVolumeMute);
        Add(result, 114, PhysicalKeys.AudioVolumeDown);
        Add(result, 115, PhysicalKeys.AudioVolumeUp);

        AddRange(result, 183, [
            PhysicalKeys.F13, PhysicalKeys.F14, PhysicalKeys.F15, PhysicalKeys.F16,
            PhysicalKeys.F17, PhysicalKeys.F18, PhysicalKeys.F19, PhysicalKeys.F20,
            PhysicalKeys.F21, PhysicalKeys.F22, PhysicalKeys.F23, PhysicalKeys.F24]);

        return result;
    }

    private static void Add(
        Dictionary<ushort, PhysicalKeyId> destination,
        int code,
        PhysicalKeyId keyId) =>
        destination.Add((ushort)code, keyId);

    private static void AddRange(
        Dictionary<ushort, PhysicalKeyId> destination,
        int firstCode,
        IReadOnlyList<PhysicalKeyId> keys)
    {
        for (var index = 0; index < keys.Count; index++)
        {
            Add(destination, firstCode + index, keys[index]);
        }
    }
}
