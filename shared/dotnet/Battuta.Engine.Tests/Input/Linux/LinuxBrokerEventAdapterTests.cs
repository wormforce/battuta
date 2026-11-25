using Battuta.Core.Input;
using Battuta.Engine.Input;
using Battuta.Engine.Input.Broker;
using Battuta.Engine.Input.Linux;

namespace Battuta.Engine.Tests.Input.Linux;

/// <summary>
/// Broker v1 message → neutral InputEvent translation. Modifier, shortcut, and reset
/// semantics mirror the Windows adapter (WindowsKeyboardEventNormalizer) so both
/// platforms deliver identical PhysicalKeyboardEvent payloads.
/// </summary>
public sealed class LinuxBrokerEventAdapterTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly LinuxBrokerEventAdapter _adapter =
        new(new FixedTimeProvider(FixedNow));

    [Fact]
    public void KeyboardPressTranslatesToPressPhase()
    {
        var input = Assert.IsType<InputEvent>(_adapter.Translate(
            new BrokerKeyboardEvent(7, 123_456, 30, BrokerProtocol.EvdevValuePress, BrokerEventOrigin.Hardware)));

        Assert.Equal(InputEventKind.Keyboard, input.Kind);
        Assert.Equal(PhysicalKeys.KeyA, input.Keyboard.Key);
        Assert.Equal(KeyPhase.Press, input.Keyboard.Phase);
        Assert.False(input.Keyboard.IsRepeat);
        Assert.Equal(7ul, input.Keyboard.Sequence);
        Assert.Equal(123_456, input.Keyboard.MonotonicTimestampNanoseconds);
        Assert.Equal(InputOrigin.Hardware, input.Keyboard.Origin);
        Assert.Equal(FixedNow, input.Keyboard.Timestamp);
        Assert.Equal(ModifierState.None, input.Keyboard.Modifiers);
        Assert.False(input.Keyboard.IsShortcutModified);
    }

    [Fact]
    public void KeyboardReleaseTranslatesToReleasePhase()
    {
        _ = _adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress));
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValueRelease)));

        Assert.Equal(KeyPhase.Release, input.Keyboard.Phase);
        Assert.False(input.Keyboard.IsRepeat);
        Assert.Equal(PhysicalKeys.KeyA, input.Keyboard.Key);
    }

    [Fact]
    public void KeyboardRepeatTranslatesToPressWithRepeatFlag()
    {
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValueRepeat)));

        Assert.Equal(KeyPhase.Press, input.Keyboard.Phase);
        Assert.True(input.Keyboard.IsRepeat);
        Assert.Equal(PhysicalKeys.KeyA, input.Keyboard.Key);
    }

    [Fact]
    public void KeyboardRepeatDoesNotChangeModifierState()
    {
        _ = _adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress)); // LeftControl
        var repeat = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValueRepeat)));

        Assert.Equal(ModifierState.LeftControl, repeat.Keyboard.Modifiers);
        Assert.True(repeat.Keyboard.IsShortcutModified);
        Assert.Equal(ModifierState.LeftControl, _adapter.PressedModifiers);

        _ = _adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress)); // LeftControl
        var release = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValueRelease)));
        Assert.Equal(ModifierState.None, release.Keyboard.Modifiers);
        Assert.Equal(ModifierState.None, _adapter.PressedModifiers);

        // A stray repeat after the release must not resurrect the modifier.
        var strayRepeat = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValueRepeat)));
        Assert.Equal(ModifierState.None, strayRepeat.Keyboard.Modifiers);
        Assert.Equal(ModifierState.None, _adapter.PressedModifiers);
    }

    [Fact]
    public void ModifierPressIncludesItselfInModifiers()
    {
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(42, BrokerProtocol.EvdevValuePress)));

        Assert.Equal(PhysicalKeys.LeftShift, input.Keyboard.Key);
        Assert.Equal(ModifierState.LeftShift, input.Keyboard.Modifiers);
        Assert.Equal(ModifierState.LeftShift, _adapter.PressedModifiers);
    }

    [Fact]
    public void ModifierAccumulationAndShortcutMirrorWindows()
    {
        // Shift alone is not a shortcut modifier (Windows shortcut mask: ctrl/alt/meta).
        var shiftPress = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(42, BrokerProtocol.EvdevValuePress)));
        Assert.False(shiftPress.Keyboard.IsShortcutModified);

        var ctrlPress = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress)));
        Assert.Equal(ModifierState.LeftShift | ModifierState.LeftControl, ctrlPress.Keyboard.Modifiers);
        Assert.True(ctrlPress.Keyboard.IsShortcutModified);

        var keyA = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress)));
        Assert.Equal(ModifierState.LeftShift | ModifierState.LeftControl, keyA.Keyboard.Modifiers);
        Assert.True(keyA.Keyboard.IsShortcutModified);

        var shiftRelease = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(42, BrokerProtocol.EvdevValueRelease)));
        Assert.Equal(ModifierState.LeftControl, shiftRelease.Keyboard.Modifiers);
        Assert.True(shiftRelease.Keyboard.IsShortcutModified);

        // The release of the last shortcut modifier reports the post-update state.
        var ctrlRelease = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValueRelease)));
        Assert.Equal(ModifierState.None, ctrlRelease.Keyboard.Modifiers);
        Assert.False(ctrlRelease.Keyboard.IsShortcutModified);
    }

    [Theory]
    [InlineData(56, ModifierState.LeftAlt)]
    [InlineData(100, ModifierState.RightAlt)]
    [InlineData(125, ModifierState.LeftMeta)]
    [InlineData(126, ModifierState.RightMeta)]
    [InlineData(97, ModifierState.RightControl)]
    [InlineData(54, ModifierState.RightShift)]
    public void RemainingModifierSidesAreTrackedIndividually(ushort code, ModifierState expected)
    {
        // LeftShift and LeftControl are covered by the accumulation tests above.
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(code, BrokerProtocol.EvdevValuePress)));

        Assert.Equal(expected, input.Keyboard.Modifiers);
        Assert.Equal(expected, _adapter.PressedModifiers);
        // Only RightShift sits outside the ctrl/alt/meta shortcut mask.
        Assert.Equal(expected is not ModifierState.RightShift, input.Keyboard.IsShortcutModified);
    }

    [Fact]
    public void RightAltCountsAsShortcutModifierOnLinux()
    {
        // Windows suppresses RightAlt while an AltGr synthetic LeftControl pair is
        // active. Evdev has no synthetic pair, so RightAlt always keeps its plain
        // shortcut-modifier meaning here.
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(100, BrokerProtocol.EvdevValuePress)));

        Assert.Equal(ModifierState.RightAlt, input.Keyboard.Modifiers);
        Assert.True(input.Keyboard.IsShortcutModified);
    }

    [Fact]
    public void SpuriousModifierReleaseDoesNotDisturbOtherModifiers()
    {
        _ = _adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress)); // LeftControl
        _ = _adapter.Translate(Keyboard(54, BrokerProtocol.EvdevValueRelease)); // RightShift was never pressed

        Assert.Equal(ModifierState.LeftControl, _adapter.PressedModifiers);
    }

    [Fact]
    public void PressedKeysAreTrackedAndReported()
    {
        _ = _adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress));
        _ = _adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress));
        _ = _adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValueRepeat));

        Assert.Equal(
            new HashSet<PhysicalKeyId> { PhysicalKeys.LeftControl, PhysicalKeys.KeyA }.OrderBy(key => key.Value),
            _adapter.PressedKeys.OrderBy(key => key.Value));

        _ = _adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValueRelease));
        Assert.Single(_adapter.PressedKeys, PhysicalKeys.LeftControl);
    }

    [Fact]
    public void ResetTranslatesToResetEventAndClearsState()
    {
        _ = _adapter.Translate(Keyboard(29, BrokerProtocol.EvdevValuePress));
        _ = _adapter.Translate(Keyboard(42, BrokerProtocol.EvdevValuePress));
        _ = _adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress));

        var input = Assert.IsType<InputEvent>(_adapter.Translate(
            new BrokerReset(77, 500, BrokerResetReason.SequenceGap)));

        Assert.Equal(InputEventKind.Reset, input.Kind);
        // Note: InputEvent.Reset(sequence) does not carry the sequence value (existing
        // Engine shape); the adapter still forwards message.Sequence to it.
        Assert.Equal(default(PhysicalKeyboardEvent), input.Keyboard);
        Assert.Equal(default(PhysicalPointerEvent), input.Pointer);
        Assert.Null(input.Application);

        Assert.Equal(ModifierState.None, _adapter.PressedModifiers);
        Assert.Empty(_adapter.PressedKeys);

        // Post-reset keys must not inherit stale modifier state.
        var keyA = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress)));
        Assert.Equal(ModifierState.None, keyA.Keyboard.Modifiers);
        Assert.False(keyA.Keyboard.IsShortcutModified);
    }

    [Theory]
    [InlineData(BrokerProtocol.EvdevBtnLeft, InputPointerButton.Primary)]
    [InlineData(BrokerProtocol.EvdevBtnRight, InputPointerButton.Secondary)]
    [InlineData(BrokerProtocol.EvdevBtnMiddle, InputPointerButton.Middle)]
    [InlineData(BrokerProtocol.EvdevBtnSide, InputPointerButton.Side)]
    [InlineData(BrokerProtocol.EvdevBtnExtra, InputPointerButton.Extra)]
    public void PointerButtonCodesMapToNeutralButtons(ushort code, InputPointerButton expected)
    {
        var press = Assert.IsType<InputEvent>(_adapter.Translate(
            new BrokerPointerButtonEvent(9, 99_999, code, BrokerProtocol.EvdevValuePress, BrokerEventOrigin.Hardware)));
        var release = Assert.IsType<InputEvent>(_adapter.Translate(
            new BrokerPointerButtonEvent(10, 100_000, code, BrokerProtocol.EvdevValueRelease, BrokerEventOrigin.Hardware)));

        Assert.Equal(InputEventKind.Pointer, press.Kind);
        Assert.Equal(expected, press.Pointer.Button);
        Assert.Equal(KeyPhase.Press, press.Pointer.Phase);
        Assert.Equal(9ul, press.Pointer.Sequence);
        Assert.Equal(99_999, press.Pointer.MonotonicTimestampNanoseconds);
        Assert.Equal(FixedNow, press.Pointer.Timestamp);
        Assert.Equal(KeyPhase.Release, release.Pointer.Phase);
    }

    [Theory]
    [InlineData(0x100)] // BTN_0, below the mouse block
    [InlineData(0x115)] // BTN_FORWARD, beyond the v1 allow list
    [InlineData(0x116)]
    [InlineData(30)] // a keyboard code is never a pointer button
    public void UnknownPointerButtonCodesYieldNull(ushort code)
    {
        Assert.Null(_adapter.Translate(
            new BrokerPointerButtonEvent(11, 100_001, code, BrokerProtocol.EvdevValuePress, BrokerEventOrigin.Hardware)));
    }

    [Theory]
    [InlineData(BrokerProtocol.EvdevValueRepeat)] // pointer frames never repeat
    [InlineData(3)]
    public void UnknownPointerValuesYieldNull(ushort value)
    {
        Assert.Null(_adapter.Translate(
            new BrokerPointerButtonEvent(12, 100_002, BrokerProtocol.EvdevBtnLeft, value, BrokerEventOrigin.Hardware)));
    }

    [Fact]
    public void NonEventMessagesYieldNull()
    {
        BrokerMessage[] nonEvents =
        [
            new BrokerHello(1, 0, 0, "battuta-test/1.0"),
            new BrokerHelloAck(1, 0, 0, 1000, "seat0"),
            new BrokerHeartbeat(13, 100_003),
            new BrokerError(BrokerErrorCode.Busy, "another subscriber is active"),
            new BrokerUnknownMessage(0xC8, [0x01]),
        ];

        foreach (var message in nonEvents)
        {
            Assert.Null(_adapter.Translate(message));
        }

        // Non-event messages must not disturb tracked state either.
        Assert.Equal(ModifierState.None, _adapter.PressedModifiers);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0xFFFF)]
    public void UnknownKeyboardValuesAreDroppedWithoutStateChange(ushort value)
    {
        Assert.Null(_adapter.Translate(Keyboard(30, value)));
        Assert.Equal(ModifierState.None, _adapter.PressedModifiers);
        Assert.Empty(_adapter.PressedKeys);
    }

    [Fact]
    public void UnknownKeyboardCodesKeepFlowingUnderStableNeutralIds()
    {
        // Contract: unknown codes are "unmapped key" (linux.evdev.<code>), never an error.
        var press = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(248, BrokerProtocol.EvdevValuePress)));
        var repeat = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(248, BrokerProtocol.EvdevValueRepeat)));

        Assert.Equal("linux.evdev.248", press.Keyboard.Key.Value);
        Assert.Equal(press.Keyboard.Key, repeat.Keyboard.Key);
        Assert.Single(_adapter.PressedKeys, new PhysicalKeyId("linux.evdev.248"));

        var release = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(248, BrokerProtocol.EvdevValueRelease)));
        Assert.Equal(press.Keyboard.Key, release.Keyboard.Key);
        Assert.Empty(_adapter.PressedKeys);
    }

    [Theory]
    [InlineData(BrokerEventOrigin.Hardware, InputOrigin.Hardware)]
    [InlineData(BrokerEventOrigin.Synthetic, InputOrigin.Synthetic)]
    [InlineData(BrokerEventOrigin.Unknown, InputOrigin.Unknown)]
    public void BrokerOriginMapsToNeutralOrigin(BrokerEventOrigin origin, InputOrigin expected)
    {
        var input = Assert.IsType<InputEvent>(_adapter.Translate(Keyboard(30, BrokerProtocol.EvdevValuePress, origin)));

        Assert.Equal(expected, input.Keyboard.Origin);
    }

    [Fact]
    public void TranslateRejectsNullMessage()
    {
        Assert.Throws<ArgumentNullException>(() => _adapter.Translate(null!));
    }

    private static BrokerKeyboardEvent Keyboard(
        ushort code,
        ushort value,
        BrokerEventOrigin origin = BrokerEventOrigin.Hardware) =>
        new(Sequence: 1, MonotonicTimestampNanoseconds: 1_000, code, value, origin);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
