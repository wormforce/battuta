namespace Battuta.Core.Input;

/// <summary>
/// Platform-neutral keyboard metadata. OS scan-code mappings live in platform projects.
/// </summary>
public static class PhysicalKeyCatalog
{
    private static readonly List<KeyboardKeyDefinition> Definitions = BuildDefinitions();
    private static readonly Dictionary<PhysicalKeyId, KeyboardKeyDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id);
    private static readonly Dictionary<string, KeyboardKeyDefinition> ByStableId =
        Definitions.ToDictionary(definition => definition.Id.Value, StringComparer.Ordinal);
    private static readonly Dictionary<string, KeyboardKeyDefinition> ByLegacyId =
        Definitions
            .Where(definition => definition.LegacySoundPackV1Id is not null)
            .ToDictionary(
                definition => definition.LegacySoundPackV1Id!,
                StringComparer.Ordinal);

    public static IReadOnlyList<KeyboardKeyDefinition> All => Definitions;

    public static IReadOnlyList<KeyboardKeyDefinition> CompactKeys { get; } =
        Definitions.Where(definition => definition.Membership == KeyboardLayoutMembership.Compact).ToArray();

    public static IReadOnlyList<KeyboardKeyDefinition> ExtendedKeys { get; } =
        Definitions.Where(definition => definition.Membership == KeyboardLayoutMembership.Extended).ToArray();

    public static bool TryGet(PhysicalKeyId id, out KeyboardKeyDefinition definition) =>
        ById.TryGetValue(id, out definition!);

    public static bool TryGetByStableId(string stableId, out KeyboardKeyDefinition definition) =>
        ByStableId.TryGetValue(stableId, out definition!);

    public static bool TryGetByLegacySoundPackV1Id(string legacyId, out KeyboardKeyDefinition definition) =>
        ByLegacyId.TryGetValue(legacyId, out definition!);

    public static KeyboardRowId RowFor(PhysicalKeyId id) =>
        TryGet(id, out var definition) ? definition.Row : KeyboardRowId.R4;

    public static KeyboardSpecialKeyId? SpecialKeyFor(PhysicalKeyId id) =>
        TryGet(id, out var definition) ? definition.SpecialKey : null;

    private static List<KeyboardKeyDefinition> BuildDefinitions()
    {
        var keys = new List<KeyboardKeyDefinition>(128);

        AddCompact(keys, "function", KeyboardRowId.R4, [
            (PhysicalKeys.Escape, "esc", 1.5, null),
            (PhysicalKeys.F1, "F1", 1d, null), (PhysicalKeys.F2, "F2", 1d, null),
            (PhysicalKeys.F3, "F3", 1d, null), (PhysicalKeys.F4, "F4", 1d, null),
            (PhysicalKeys.F5, "F5", 1d, null), (PhysicalKeys.F6, "F6", 1d, null),
            (PhysicalKeys.F7, "F7", 1d, null), (PhysicalKeys.F8, "F8", 1d, null),
            (PhysicalKeys.F9, "F9", 1d, null), (PhysicalKeys.F10, "F10", 1d, null),
            (PhysicalKeys.F11, "F11", 1d, null), (PhysicalKeys.F12, "F12", 1d, null),
        ]);

        AddCompact(keys, "number", KeyboardRowId.R0, [
            (PhysicalKeys.Backquote, "`", 1d, null),
            (PhysicalKeys.Digit1, "1", 1d, null), (PhysicalKeys.Digit2, "2", 1d, null),
            (PhysicalKeys.Digit3, "3", 1d, null), (PhysicalKeys.Digit4, "4", 1d, null),
            (PhysicalKeys.Digit5, "5", 1d, null), (PhysicalKeys.Digit6, "6", 1d, null),
            (PhysicalKeys.Digit7, "7", 1d, null), (PhysicalKeys.Digit8, "8", 1d, null),
            (PhysicalKeys.Digit9, "9", 1d, null), (PhysicalKeys.Digit0, "0", 1d, null),
            (PhysicalKeys.Minus, "-", 1d, null), (PhysicalKeys.Equal, "=", 1d, null),
            (PhysicalKeys.Backspace, "delete", 1.5, KeyboardSpecialKeyId.Backspace),
        ]);

        AddCompact(keys, "qwerty", KeyboardRowId.R1, [
            (PhysicalKeys.Tab, "tab", 1.5, null),
            (PhysicalKeys.KeyQ, "Q", 1d, null), (PhysicalKeys.KeyW, "W", 1d, null),
            (PhysicalKeys.KeyE, "E", 1d, null), (PhysicalKeys.KeyR, "R", 1d, null),
            (PhysicalKeys.KeyT, "T", 1d, null), (PhysicalKeys.KeyY, "Y", 1d, null),
            (PhysicalKeys.KeyU, "U", 1d, null), (PhysicalKeys.KeyI, "I", 1d, null),
            (PhysicalKeys.KeyO, "O", 1d, null), (PhysicalKeys.KeyP, "P", 1d, null),
            (PhysicalKeys.LeftBracket, "[", 1d, null),
            (PhysicalKeys.RightBracket, "]", 1d, null),
            (PhysicalKeys.Backslash, "\\", 1d, null),
        ]);

        AddCompact(keys, "home", KeyboardRowId.R2, [
            (PhysicalKeys.CapsLock, "caps lock", 1.75, null),
            (PhysicalKeys.KeyA, "A", 1d, null), (PhysicalKeys.KeyS, "S", 1d, null),
            (PhysicalKeys.KeyD, "D", 1d, null), (PhysicalKeys.KeyF, "F", 1d, null),
            (PhysicalKeys.KeyG, "G", 1d, null), (PhysicalKeys.KeyH, "H", 1d, null),
            (PhysicalKeys.KeyJ, "J", 1d, null), (PhysicalKeys.KeyK, "K", 1d, null),
            (PhysicalKeys.KeyL, "L", 1d, null), (PhysicalKeys.Semicolon, ";", 1d, null),
            (PhysicalKeys.Quote, "'", 1d, null),
            (PhysicalKeys.Enter, "return", 1.75, KeyboardSpecialKeyId.Enter),
        ]);

        AddCompact(keys, "zxcv", KeyboardRowId.R3, [
            (PhysicalKeys.LeftShift, "shift", 2.25, null),
            (PhysicalKeys.KeyZ, "Z", 1d, null), (PhysicalKeys.KeyX, "X", 1d, null),
            (PhysicalKeys.KeyC, "C", 1d, null), (PhysicalKeys.KeyV, "V", 1d, null),
            (PhysicalKeys.KeyB, "B", 1d, null), (PhysicalKeys.KeyN, "N", 1d, null),
            (PhysicalKeys.KeyM, "M", 1d, null), (PhysicalKeys.Comma, ",", 1d, null),
            (PhysicalKeys.Period, ".", 1d, null), (PhysicalKeys.Slash, "/", 1d, null),
            (PhysicalKeys.RightShift, "shift", 2.25, null),
        ]);

        AddCompact(keys, "bottom", KeyboardRowId.R4, [
            (PhysicalKeys.Fn, "fn", 1d, null),
            (PhysicalKeys.LeftControl, "control", 1d, null),
            (PhysicalKeys.LeftAlt, "option", 1d, null),
            (PhysicalKeys.LeftMeta, "command", 1.25, null),
            (PhysicalKeys.Space, "space", 5d, KeyboardSpecialKeyId.Space),
            (PhysicalKeys.RightMeta, "command", 1.25, null),
            (PhysicalKeys.RightAlt, "option", 1d, null),
            (PhysicalKeys.RightControl, "control", 1d, null),
            (PhysicalKeys.ArrowLeft, "←", 1d, null),
            (PhysicalKeys.ArrowUp, "↑", 1d, null),
            (PhysicalKeys.ArrowDown, "↓", 1d, null),
            (PhysicalKeys.ArrowRight, "→", 1d, null),
        ]);

        AddExtended(keys, "navigation", [
            (PhysicalKeys.Insert, "help", "extended.help", null, 1d),
            (PhysicalKeys.Home, "home", "extended.home", null, 1d),
            (PhysicalKeys.PageUp, "page up", "extended.pageUp", null, 1d),
            (PhysicalKeys.Delete, "⌦", "extended.forwardDelete", KeyboardSpecialKeyId.Backspace, 1d),
            (PhysicalKeys.End, "end", "extended.end", null, 1d),
            (PhysicalKeys.PageDown, "page down", "extended.pageDown", null, 1d),
        ]);
        AddExtended(keys, "extendedFunction", [
            (PhysicalKeys.F13, "F13", "extended.f13", null, 1d),
            (PhysicalKeys.F14, "F14", "extended.f14", null, 1d),
            (PhysicalKeys.F15, "F15", "extended.f15", null, 1d),
            (PhysicalKeys.F16, "F16", "extended.f16", null, 1d),
            (PhysicalKeys.F17, "F17", "extended.f17", null, 1d),
            (PhysicalKeys.F18, "F18", "extended.f18", null, 1d),
            (PhysicalKeys.F19, "F19", "extended.f19", null, 1d),
            (PhysicalKeys.F20, "F20", "extended.f20", null, 1d),
        ]);
        AddExtended(keys, "keypadTop", [
            (PhysicalKeys.NumLock, "clear", "extended.keypadClear", null, 1d),
            (PhysicalKeys.NumpadEqual, "=", "extended.keypadEqual", null, 1d),
            (PhysicalKeys.NumpadDivide, "÷", "extended.keypadDivide", null, 1d),
            (PhysicalKeys.NumpadMultiply, "×", "extended.keypadMultiply", null, 1d),
            (PhysicalKeys.NumpadSubtract, "−", "extended.keypadMinus", null, 1d),
        ]);
        AddExtended(keys, "keypadUpper", [
            (PhysicalKeys.Numpad7, "7", "extended.keypad7", null, 1d),
            (PhysicalKeys.Numpad8, "8", "extended.keypad8", null, 1d),
            (PhysicalKeys.Numpad9, "9", "extended.keypad9", null, 1d),
            (PhysicalKeys.NumpadAdd, "+", "extended.keypadPlus", null, 1d),
        ]);
        AddExtended(keys, "keypadMiddle", [
            (PhysicalKeys.Numpad4, "4", "extended.keypad4", null, 1d),
            (PhysicalKeys.Numpad5, "5", "extended.keypad5", null, 1d),
            (PhysicalKeys.Numpad6, "6", "extended.keypad6", null, 1d),
        ]);
        AddExtended(keys, "keypadLower", [
            (PhysicalKeys.Numpad1, "1", "extended.keypad1", null, 1d),
            (PhysicalKeys.Numpad2, "2", "extended.keypad2", null, 1d),
            (PhysicalKeys.Numpad3, "3", "extended.keypad3", null, 1d),
            (PhysicalKeys.NumpadEnter, "enter", "extended.keypadEnter", KeyboardSpecialKeyId.Enter, 1.5),
        ]);
        AddExtended(keys, "keypadBottom", [
            (PhysicalKeys.Numpad0, "0", "extended.keypad0", null, 2d),
            (PhysicalKeys.NumpadDecimal, ".", "extended.keypadDecimal", null, 1d),
        ]);
        AddExtended(keys, "international", [
            (PhysicalKeys.IntlBackslash, "§/±", "extended.isoSection", null, 1d),
            (PhysicalKeys.IntlYen, "¥", "extended.jisYen", null, 1d),
            (PhysicalKeys.IntlRo, "＿", "extended.jisUnderscore", null, 1d),
            (PhysicalKeys.NumpadComma, "，", "extended.jisKeypadComma", null, 1d),
            (PhysicalKeys.Eisu, "英数", "extended.jisEisu", null, 1d),
            (PhysicalKeys.Kana, "かな", "extended.jisKana", null, 1d),
        ]);
        foreach (var (id, label, legacyId) in new[]
        {
            (PhysicalKeys.ZenkakuHankaku, "半角/全角", "extended.jisZenkakuHankaku"),
            (PhysicalKeys.Henkan, "変換", "extended.jisHenkan"),
            (PhysicalKeys.Muhenkan, "無変換", "extended.jisMuhenkan"),
        })
        {
            keys.Add(new KeyboardKeyDefinition(
                id, label, KeyboardRowId.R4, null, 1d, legacyId,
                KeyboardLayoutMembership.InputMethod, "international"));
        }
        AddExtended(keys, "media", [
            (PhysicalKeys.AudioVolumeUp, "音量+", "extended.volumeUp", null, 1.5),
            (PhysicalKeys.AudioVolumeDown, "音量−", "extended.volumeDown", null, 1.5),
            (PhysicalKeys.AudioVolumeMute, "静音", "extended.mute", null, 1.5),
        ]);

        foreach (var (id, label) in new[]
        {
            (PhysicalKeys.F21, "F21"), (PhysicalKeys.F22, "F22"),
            (PhysicalKeys.F23, "F23"), (PhysicalKeys.F24, "F24"),
            (PhysicalKeys.PrintScreen, "Print Screen"),
            (PhysicalKeys.ScrollLock, "Scroll Lock"),
            (PhysicalKeys.Pause, "Pause"),
            (PhysicalKeys.ContextMenu, "Menu"),
        })
        {
            keys.Add(new KeyboardKeyDefinition(
                id, label, KeyboardRowId.R4, null, 1, null,
                KeyboardLayoutMembership.PlatformOnly, "platform"));
        }

        return keys;
    }

    private static void AddCompact(
        List<KeyboardKeyDefinition> destination,
        string rowId,
        KeyboardRowId defaultRow,
        IEnumerable<(PhysicalKeyId Id, string Label, double Width, KeyboardSpecialKeyId? Special)> keys)
    {
        foreach (var (id, label, width, special) in keys)
        {
            var row = special is not null
                || id == PhysicalKeys.Tab
                || id == PhysicalKeys.CapsLock
                || id == PhysicalKeys.LeftShift
                || id == PhysicalKeys.RightShift
                ? KeyboardRowId.R4
                : defaultRow;
            destination.Add(new KeyboardKeyDefinition(
                id,
                label,
                row,
                special,
                width,
                LegacyIdForCompactKey(id),
                KeyboardLayoutMembership.Compact,
                rowId));
        }
    }

    private static void AddExtended(
        List<KeyboardKeyDefinition> destination,
        string rowId,
        IEnumerable<(PhysicalKeyId Id, string Label, string LegacyId, KeyboardSpecialKeyId? Special, double Width)> keys)
    {
        foreach (var (id, label, legacyId, special, width) in keys)
        {
            destination.Add(new KeyboardKeyDefinition(
                id,
                label,
                KeyboardRowId.R4,
                special,
                width,
                legacyId,
                KeyboardLayoutMembership.Extended,
                $"extended.{rowId}"));
        }
    }

    private static string LegacyIdForCompactKey(PhysicalKeyId id)
    {
        if (id.Value.StartsWith("Key", StringComparison.Ordinal) && id.Value.Length == 4)
        {
            return id.Value[3..].ToLowerInvariant();
        }

        return id.Value switch
        {
            "LeftAlt" => "leftOption",
            "RightAlt" => "rightOption",
            "LeftMeta" => "leftCommand",
            "RightMeta" => "rightCommand",
            "Fn" => "function",
            "ArrowLeft" => "leftArrow",
            "ArrowUp" => "upArrow",
            "ArrowDown" => "downArrow",
            "ArrowRight" => "rightArrow",
            _ => char.ToLowerInvariant(id.Value[0]) + id.Value[1..],
        };
    }
}

public static class KeyboardLayoutCatalog
{
    public const string DefaultLayoutId = "mac-ansi-tkl-v1";

    private static readonly string[] CompactRowOrder =
        ["function", "number", "qwerty", "home", "zxcv", "bottom"];

    public static KeyboardLayoutDefinition CompactAnsi { get; } = new(
        DefaultLayoutId,
        "Mac US ANSI 紧凑型",
        CompactRowOrder.Select(rowId => new KeyboardLayoutRow(
            rowId,
            PhysicalKeyCatalog.CompactKeys.Where(key => key.LayoutRowId == rowId).ToArray())).ToArray());

    public static IReadOnlyList<KeyboardLayoutRow> ExtendedRows { get; } =
        PhysicalKeyCatalog.ExtendedKeys
            .GroupBy(key => key.LayoutRowId)
            .Select(group => new KeyboardLayoutRow(group.Key, group.ToArray()))
            .ToArray();
}
