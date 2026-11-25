using Battuta.Core.Input;
using Battuta.Engine.Input.Linux;

namespace Battuta.Engine.Tests.Input.Linux;

/// <summary>
/// Table coverage for the evdev key mapper. Sampled entries pin the authoritative
/// numbers from /usr/include/linux/input-event-codes.h; the whole-table walks enforce
/// uniqueness and catalog alignment for every code.
/// </summary>
public sealed class LinuxEvdevKeyMapperTests
{
    [Theory]
    [InlineData(1, "Escape")]
    [InlineData(2, "Digit1")]
    [InlineData(11, "Digit0")]
    [InlineData(12, "Minus")]
    [InlineData(13, "Equal")]
    [InlineData(14, "Backspace")]
    [InlineData(15, "Tab")]
    [InlineData(16, "KeyQ")]
    [InlineData(25, "KeyP")]
    [InlineData(26, "LeftBracket")]
    [InlineData(27, "RightBracket")]
    [InlineData(28, "Enter")]
    [InlineData(30, "KeyA")]
    [InlineData(38, "KeyL")]
    [InlineData(39, "Semicolon")]
    [InlineData(40, "Quote")]
    [InlineData(41, "Backquote")]
    [InlineData(43, "Backslash")]
    [InlineData(44, "KeyZ")]
    [InlineData(50, "KeyM")]
    [InlineData(51, "Comma")]
    [InlineData(52, "Period")]
    [InlineData(53, "Slash")]
    [InlineData(57, "Space")]
    [InlineData(58, "CapsLock")]
    [InlineData(59, "F1")]
    [InlineData(68, "F10")]
    [InlineData(87, "F11")]
    [InlineData(88, "F12")]
    [InlineData(183, "F13")]
    [InlineData(194, "F24")]
    [InlineData(69, "NumLock")]
    [InlineData(70, "ScrollLock")]
    [InlineData(99, "PrintScreen")] // KEY_SYSRQ, the PC PrintScreen position
    [InlineData(119, "Pause")]
    [InlineData(127, "ContextMenu")] // KEY_COMPOSE, the PC "Menu" key
    [InlineData(102, "Home")]
    [InlineData(103, "ArrowUp")]
    [InlineData(104, "PageUp")]
    [InlineData(105, "ArrowLeft")]
    [InlineData(106, "ArrowRight")]
    [InlineData(107, "End")]
    [InlineData(108, "ArrowDown")]
    [InlineData(109, "PageDown")]
    [InlineData(110, "Insert")]
    [InlineData(111, "Delete")]
    [InlineData(55, "NumpadMultiply")]
    [InlineData(71, "Numpad7")]
    [InlineData(74, "NumpadSubtract")]
    [InlineData(78, "NumpadAdd")]
    [InlineData(79, "Numpad1")]
    [InlineData(82, "Numpad0")]
    [InlineData(83, "NumpadDecimal")]
    [InlineData(96, "NumpadEnter")]
    [InlineData(98, "NumpadDivide")]
    [InlineData(117, "NumpadEqual")]
    [InlineData(95, "NumpadComma")] // KEY_KPJPCOMMA, the JIS keypad comma
    [InlineData(113, "AudioVolumeMute")]
    [InlineData(114, "AudioVolumeDown")]
    [InlineData(115, "AudioVolumeUp")]
    public void MappedEvdevCodesResolveToCatalogIds(ushort code, string expectedStableId)
    {
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(code, out var keyId));
        Assert.True(LinuxEvdevKeyMapper.IsKeyboardKeyCode(code));
        Assert.Equal(expectedStableId, keyId.Value);
        Assert.True(PhysicalKeyCatalog.TryGet(keyId, out _));
    }

    [Theory]
    [InlineData(42, ModifierState.LeftShift)] // KEY_LEFTSHIFT
    [InlineData(54, ModifierState.RightShift)] // KEY_RIGHTSHIFT
    [InlineData(29, ModifierState.LeftControl)] // KEY_LEFTCTRL
    [InlineData(97, ModifierState.RightControl)] // KEY_RIGHTCTRL
    [InlineData(56, ModifierState.LeftAlt)] // KEY_LEFTALT
    [InlineData(100, ModifierState.RightAlt)] // KEY_RIGHTALT
    [InlineData(125, ModifierState.LeftMeta)] // KEY_LEFTMETA
    [InlineData(126, ModifierState.RightMeta)] // KEY_RIGHTMETA
    public void LeftAndRightModifiersMapToDistinctFlags(ushort code, ModifierState expected)
    {
        Assert.True(LinuxEvdevKeyMapper.TryGetModifier(code, out var modifier));
        Assert.Equal(expected, modifier);
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(code, out _));
    }

    [Fact]
    public void NonModifierCodesHaveNoModifierFlag()
    {
        // CapsLock, a letter, the Menu key, and the keypad slash are not modifiers.
        foreach (var code in new ushort[] { 58, 30, 127, 98 })
        {
            Assert.False(LinuxEvdevKeyMapper.TryGetModifier(code, out _));
        }
    }

    [Fact]
    public void EnterAndKeypadEnterRemainDistinct()
    {
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(28, out var enter));
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(96, out var keypadEnter));
        Assert.Equal(PhysicalKeys.Enter, enter);
        Assert.Equal(PhysicalKeys.NumpadEnter, keypadEnter);
        Assert.NotEqual(enter, keypadEnter);
    }

    [Fact]
    public void Key102ndIsNotRightAlt()
    {
        // The uapi header places KEY_102ND at 86 and KEY_RIGHTALT at 100; the two
        // must never collapse into each other.
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(86, out var key102nd));
        Assert.Equal(PhysicalKeys.IntlBackslash, key102nd);
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(100, out var rightAlt));
        Assert.Equal(PhysicalKeys.RightAlt, rightAlt);
        Assert.NotEqual(key102nd, rightAlt);
    }

    [Theory]
    [InlineData(85, "ZenkakuHankaku")] // KEY_ZENKAKUHANKAKU
    [InlineData(89, "IntlRo")] // KEY_RO
    [InlineData(92, "Henkan")] // KEY_HENKAN
    [InlineData(93, "Kana")] // KEY_KATAKANAHIRAGANA
    [InlineData(94, "Muhenkan")] // KEY_MUHENKAN
    [InlineData(124, "IntlYen")] // KEY_YEN
    [InlineData(86, "IntlBackslash")] // KEY_102ND
    public void JapaneseKeyboardKeysMapToCatalogIds(ushort code, string expectedStableId)
    {
        Assert.True(LinuxEvdevKeyMapper.TryGetKeyId(code, out var keyId));
        Assert.Equal(expectedStableId, keyId.Value);
    }

    [Fact]
    public void UnknownKeyIdsAreStableAndDistinct()
    {
        var first = LinuxEvdevKeyMapper.GetUnknownKeyId(248);
        var again = LinuxEvdevKeyMapper.GetUnknownKeyId(248);
        var other = LinuxEvdevKeyMapper.GetUnknownKeyId(249);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal("linux.evdev.248", first.Value);
        Assert.Equal("linux.evdev.249", other.Value);
        Assert.True(first.IsValid);
        Assert.False(PhysicalKeyCatalog.TryGet(first, out _));
    }

    [Fact]
    public void EveryMappedCodeResolvesToExactlyOneDistinctCatalogId()
    {
        var mappedIds = new HashSet<PhysicalKeyId>();
        var mappedCodeCount = 0;

        for (var code = 0; code <= ushort.MaxValue; code++)
        {
            Assert.Equal(
                LinuxEvdevKeyMapper.IsKeyboardKeyCode((ushort)code),
                LinuxEvdevKeyMapper.TryGetKeyId((ushort)code, out var keyId));
            if (!LinuxEvdevKeyMapper.TryGetKeyId((ushort)code, out keyId))
            {
                continue;
            }

            mappedCodeCount++;
            Assert.True(PhysicalKeyCatalog.TryGet(keyId, out _), $"{keyId} is not a catalog key.");
            Assert.True(mappedIds.Add(keyId), $"Duplicate key id {keyId} in the evdev table.");
        }

        Assert.True(mappedCodeCount >= 100);
    }

    [Fact]
    public void EveryCatalogKeyHasAnEvdevCodeExceptFirmwareOnlyKeys()
    {
        // Fn never reaches the OS (handled in laptop firmware) and Eisu has no
        // standard evdev code, so they stay platform-adapter specific.
        var firmwareOnlyKeys = new HashSet<PhysicalKeyId>
        {
            PhysicalKeys.Fn,
            PhysicalKeys.Eisu,
        };

        var mappedIds = new HashSet<PhysicalKeyId>();
        for (var code = 0; code <= ushort.MaxValue; code++)
        {
            if (LinuxEvdevKeyMapper.TryGetKeyId((ushort)code, out var keyId))
            {
                Assert.True(mappedIds.Add(keyId), $"Duplicate key id {keyId} in the evdev table.");
            }
        }

        foreach (var definition in PhysicalKeyCatalog.All)
        {
            if (firmwareOnlyKeys.Contains(definition.Id))
            {
                Assert.DoesNotContain(definition.Id, mappedIds);
                continue;
            }

            Assert.True(
                mappedIds.Contains(definition.Id),
                $"Catalog key {definition.Id.Value} has no evdev mapping.");
        }
    }

    [Fact]
    public void NonKeyboardCodesStayUnmapped()
    {
        // KEY_RESERVED, KEY_MACRO, KEY_POWER, KEY_LINEFEED, the AC Menu key, and the
        // BTN_* pointer namespace are deliberately outside the keyboard table.
        foreach (var code in new ushort[] { 0, 101, 112, 116, 139, 0x110, 0x111, 0x114, 0x116 })
        {
            Assert.False(LinuxEvdevKeyMapper.TryGetKeyId(code, out _), $"code {code}");
            Assert.False(LinuxEvdevKeyMapper.IsKeyboardKeyCode(code), $"code {code}");
        }
    }
}
