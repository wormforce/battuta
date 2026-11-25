namespace Battuta.Core.Input;

/// <summary>
/// Windows-facing presentation metadata for one known physical key.
/// </summary>
/// <remarks>
/// <see cref="Id"/> remains the identity. Labels are presentation only and must
/// never be used for event routing, statistics, or DIY manifest keys.
/// </remarks>
public sealed record WindowsKeyDisplayDefinition(
    PhysicalKeyId Id,
    string Label,
    KeyboardRowId SoundRow,
    KeyboardSpecialKeyId? SpecialKey,
    bool IsDiyAssignable,
    bool IsUniversallyObservable);

/// <summary>Windows terminology layered over the platform-neutral key catalog.</summary>
public static class WindowsKeyDisplayCatalog
{
    private static readonly WindowsKeyDisplayDefinition[] Definitions = PhysicalKeyCatalog.All
        .Where(key => key.Membership != KeyboardLayoutMembership.InputMethod)
        .Select(key => new WindowsKeyDisplayDefinition(
            key.Id,
            LabelForKnownKey(key.Id),
            key.Row,
            key.SpecialKey,
            key.LegacySoundPackV1Id is not null,
            key.Id != PhysicalKeys.Fn))
        .ToArray();

    private static readonly Dictionary<PhysicalKeyId, WindowsKeyDisplayDefinition> ById =
        Definitions.ToDictionary(key => key.Id);

    public static IReadOnlyList<WindowsKeyDisplayDefinition> All => Definitions;

    public static bool TryGet(
        PhysicalKeyId id,
        out WindowsKeyDisplayDefinition definition) =>
        ById.TryGetValue(id, out definition!);

    public static WindowsKeyDisplayDefinition Get(PhysicalKeyId id) =>
        TryGet(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown physical key: {id.Value}");

    public static string LabelFor(PhysicalKeyId id) => Get(id).Label;

    private static string LabelForKnownKey(PhysicalKeyId id)
    {
        var value = id.Value;
        if (value.Length == 4 && value.StartsWith("Key", StringComparison.Ordinal))
        {
            return value[3..];
        }

        if (value.StartsWith("Digit", StringComparison.Ordinal))
        {
            return value[5..];
        }

        if (value.StartsWith('F')
            && int.TryParse(value.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            return value;
        }

        if (value.StartsWith("Numpad", StringComparison.Ordinal)
            && value.Length == 7
            && char.IsAsciiDigit(value[6]))
        {
            return $"Num {value[6]}";
        }

        return id == PhysicalKeys.Escape ? "Esc"
            : id == PhysicalKeys.Backquote ? "`"
            : id == PhysicalKeys.Minus ? "-"
            : id == PhysicalKeys.Equal ? "="
            : id == PhysicalKeys.Backspace ? "Backspace"
            : id == PhysicalKeys.Tab ? "Tab"
            : id == PhysicalKeys.LeftBracket ? "["
            : id == PhysicalKeys.RightBracket ? "]"
            : id == PhysicalKeys.Backslash ? "\\"
            : id == PhysicalKeys.CapsLock ? "Caps Lock"
            : id == PhysicalKeys.Semicolon ? ";"
            : id == PhysicalKeys.Quote ? "'"
            : id == PhysicalKeys.Enter ? "Enter"
            : id is var shift && (shift == PhysicalKeys.LeftShift || shift == PhysicalKeys.RightShift) ? "Shift"
            : id == PhysicalKeys.Comma ? ","
            : id == PhysicalKeys.Period ? "."
            : id == PhysicalKeys.Slash ? "/"
            : id == PhysicalKeys.Fn ? "Fn"
            : id is var control && (control == PhysicalKeys.LeftControl || control == PhysicalKeys.RightControl) ? "Ctrl"
            : id is var alt && (alt == PhysicalKeys.LeftAlt || alt == PhysicalKeys.RightAlt) ? "Alt"
            : id is var meta && (meta == PhysicalKeys.LeftMeta || meta == PhysicalKeys.RightMeta) ? "Win"
            : id == PhysicalKeys.Space ? "Space"
            : id == PhysicalKeys.ArrowLeft ? "←"
            : id == PhysicalKeys.ArrowUp ? "↑"
            : id == PhysicalKeys.ArrowDown ? "↓"
            : id == PhysicalKeys.ArrowRight ? "→"
            : id == PhysicalKeys.Insert ? "Insert"
            : id == PhysicalKeys.Home ? "Home"
            : id == PhysicalKeys.PageUp ? "Page Up"
            : id == PhysicalKeys.Delete ? "Delete"
            : id == PhysicalKeys.End ? "End"
            : id == PhysicalKeys.PageDown ? "Page Down"
            : id == PhysicalKeys.PrintScreen ? "Print Screen"
            : id == PhysicalKeys.ScrollLock ? "Scroll Lock"
            : id == PhysicalKeys.Pause ? "Pause"
            : id == PhysicalKeys.ContextMenu ? "Menu"
            : id == PhysicalKeys.NumLock ? "Num Lock"
            : id == PhysicalKeys.NumpadEqual ? "Num ="
            : id == PhysicalKeys.NumpadDivide ? "Num /"
            : id == PhysicalKeys.NumpadMultiply ? "Num *"
            : id == PhysicalKeys.NumpadSubtract ? "Num -"
            : id == PhysicalKeys.NumpadAdd ? "Num +"
            : id == PhysicalKeys.NumpadEnter ? "Num Enter"
            : id == PhysicalKeys.NumpadDecimal ? "Num ."
            : id == PhysicalKeys.NumpadComma ? "Num ,"
            : id == PhysicalKeys.IntlBackslash ? "Intl \\"
            : id == PhysicalKeys.IntlYen ? "Yen (¥)"
            : id == PhysicalKeys.IntlRo ? "Intl Ro"
            : id == PhysicalKeys.Eisu ? "Eisu"
            : id == PhysicalKeys.Kana ? "Kana"
            : id == PhysicalKeys.AudioVolumeUp ? "Volume Up"
            : id == PhysicalKeys.AudioVolumeDown ? "Volume Down"
            : id == PhysicalKeys.AudioVolumeMute ? "Mute"
            : value;
    }
}

/// <summary>
/// A 14.5U Windows ANSI presentation layout derived from the Battuta compact
/// geometry. This is not a sound-pack schema ID and does not replace the
/// legacy <c>mac-ansi-tkl-v1</c> manifest layout.
/// </summary>
public static class WindowsAnsiVisualLayoutCatalog
{
    public const string CompactLayoutId = "windows-ansi-compact-14.5u-v1";

    public static KeyboardVisualLayout CompactAnsi { get; } = BuildCompactAnsi();

    public static IReadOnlyList<WindowsKeyDisplayDefinition> MainKeys { get; } =
        CompactAnsi.Placements
            .Select(placement => placement.KeyId!.Value)
            .Distinct()
            .Select(WindowsKeyDisplayCatalog.Get)
            .ToArray();

    /// <summary>Known Windows keys outside the compact visual keyboard.</summary>
    public static IReadOnlyList<WindowsKeyDisplayDefinition> ExtendedKeys { get; } =
        WindowsKeyDisplayCatalog.All
            .Where(key => !CompactAnsi.KeyIds.Contains(key.Id) && key.Id != PhysicalKeys.Fn)
            .ToArray();

    /// <summary>
    /// Fn is deliberately outside the standard Windows layout because Windows
    /// does not expose a universal low-level-hook scan code for it.
    /// </summary>
    public static WindowsKeyDisplayDefinition DeviceSpecificFn { get; } =
        WindowsKeyDisplayCatalog.Get(PhysicalKeys.Fn);

    private static KeyboardVisualLayout BuildCompactAnsi()
    {
        var rows = new Slot[][]
        {
            [
                Key(PhysicalKeys.Escape, 1.5),
                Key(PhysicalKeys.F1), Key(PhysicalKeys.F2), Key(PhysicalKeys.F3), Key(PhysicalKeys.F4),
                Key(PhysicalKeys.F5), Key(PhysicalKeys.F6), Key(PhysicalKeys.F7), Key(PhysicalKeys.F8),
                Key(PhysicalKeys.F9), Key(PhysicalKeys.F10), Key(PhysicalKeys.F11), Key(PhysicalKeys.F12),
                Key(PhysicalKeys.PrintScreen),
            ],
            [
                Key(PhysicalKeys.Backquote),
                Key(PhysicalKeys.Digit1), Key(PhysicalKeys.Digit2), Key(PhysicalKeys.Digit3),
                Key(PhysicalKeys.Digit4), Key(PhysicalKeys.Digit5), Key(PhysicalKeys.Digit6),
                Key(PhysicalKeys.Digit7), Key(PhysicalKeys.Digit8), Key(PhysicalKeys.Digit9),
                Key(PhysicalKeys.Digit0), Key(PhysicalKeys.Minus), Key(PhysicalKeys.Equal),
                Key(PhysicalKeys.Backspace, 1.5),
            ],
            [
                Key(PhysicalKeys.Tab, 1.5),
                Key(PhysicalKeys.KeyQ), Key(PhysicalKeys.KeyW), Key(PhysicalKeys.KeyE),
                Key(PhysicalKeys.KeyR), Key(PhysicalKeys.KeyT), Key(PhysicalKeys.KeyY),
                Key(PhysicalKeys.KeyU), Key(PhysicalKeys.KeyI), Key(PhysicalKeys.KeyO),
                Key(PhysicalKeys.KeyP), Key(PhysicalKeys.LeftBracket),
                Key(PhysicalKeys.RightBracket), Key(PhysicalKeys.Backslash),
            ],
            [
                Key(PhysicalKeys.CapsLock, 1.75),
                Key(PhysicalKeys.KeyA), Key(PhysicalKeys.KeyS), Key(PhysicalKeys.KeyD),
                Key(PhysicalKeys.KeyF), Key(PhysicalKeys.KeyG), Key(PhysicalKeys.KeyH),
                Key(PhysicalKeys.KeyJ), Key(PhysicalKeys.KeyK), Key(PhysicalKeys.KeyL),
                Key(PhysicalKeys.Semicolon), Key(PhysicalKeys.Quote),
                Key(PhysicalKeys.Enter, 1.75),
            ],
            [
                Key(PhysicalKeys.LeftShift, 2.25),
                Key(PhysicalKeys.KeyZ), Key(PhysicalKeys.KeyX), Key(PhysicalKeys.KeyC),
                Key(PhysicalKeys.KeyV), Key(PhysicalKeys.KeyB), Key(PhysicalKeys.KeyN),
                Key(PhysicalKeys.KeyM), Key(PhysicalKeys.Comma), Key(PhysicalKeys.Period),
                Key(PhysicalKeys.Slash), Key(PhysicalKeys.RightShift, 2.25),
            ],
            [
                Key(PhysicalKeys.LeftControl), Key(PhysicalKeys.LeftMeta),
                Key(PhysicalKeys.LeftAlt, 1.25), Key(PhysicalKeys.Space, 5),
                Key(PhysicalKeys.RightAlt, 1.25), Key(PhysicalKeys.RightMeta),
                Key(PhysicalKeys.RightControl), Key(PhysicalKeys.ArrowLeft),
                Stacked(PhysicalKeys.ArrowUp, PhysicalKeys.ArrowDown),
                Key(PhysicalKeys.ArrowRight),
            ],
        };

        var placements = new List<KeyboardVisualPlacement>(78);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var xUnits = 0d;
            foreach (var slot in rows[rowIndex])
            {
                if (slot is KeySlot key)
                {
                    placements.Add(Placement(
                        key.Id,
                        rowIndex,
                        xUnits,
                        key.Width,
                        KeyboardVisualVerticalSlot.Full));
                }
                else if (slot is StackedSlot stacked)
                {
                    placements.Add(Placement(
                        stacked.Upper,
                        rowIndex,
                        xUnits,
                        stacked.Width,
                        KeyboardVisualVerticalSlot.UpperHalf));
                    placements.Add(Placement(
                        stacked.Lower,
                        rowIndex,
                        xUnits,
                        stacked.Width,
                        KeyboardVisualVerticalSlot.LowerHalf));
                }

                xUnits += slot.Width;
            }

            if (Math.Abs(xUnits - 14.5) > 0.0001)
            {
                throw new InvalidOperationException(
                    $"Windows ANSI visual row {rowIndex} must span 14.5U.");
            }
        }

        return new KeyboardVisualLayout(
            CompactLayoutId,
            14.5,
            rows.Length,
            placements);
    }

    private static KeyboardVisualPlacement Placement(
        PhysicalKeyId id,
        int row,
        double xUnits,
        double widthUnits,
        KeyboardVisualVerticalSlot verticalSlot) =>
        new(
            $"windows.key.{id.Value}",
            id,
            WindowsKeyDisplayCatalog.LabelFor(id),
            null,
            row,
            xUnits,
            widthUnits,
            verticalSlot);

    private static KeySlot Key(PhysicalKeyId id, double width = 1) => new(id, width);

    private static StackedSlot Stacked(
        PhysicalKeyId upper,
        PhysicalKeyId lower,
        double width = 1) => new(upper, lower, width);

    private abstract record Slot(double Width);
    private sealed record KeySlot(PhysicalKeyId Id, double KeyWidth) : Slot(KeyWidth);
    private sealed record StackedSlot(
        PhysicalKeyId Upper,
        PhysicalKeyId Lower,
        double StackWidth) : Slot(StackWidth);
}
