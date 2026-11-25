using Battuta.Core.Input;
using Battuta.Engine.Input.Broker;

namespace Battuta.Engine.Input.Linux;
/// <summary>
/// Converts broker v1 messages (see shared/contracts/LINUX_INPUT_BROKER_PROTOCOL.md)
/// into neutral <see cref="InputEvent"/>s for the Linux composition root. One instance
/// belongs to one seat/session: it tracks pressed modifiers across events, so callers
/// must translate from the single broker receive loop, like the Windows hook consumer.
/// </summary>
/// <remarks>
/// Modifier and shortcut semantics mirror <c>WindowsKeyboardEventNormalizer</c> exactly:
/// pressed state is updated before the event is built, so a modifier press event carries
/// itself in <see cref="PhysicalKeyboardEvent.Modifiers"/>, and
/// <see cref="PhysicalKeyboardEvent.IsShortcutModified"/> reflects the ctrl/alt/meta bits
/// of the post-update pressed set. Windows additionally suppresses the synthetic
/// LeftControl of an AltGr pair; evdev reports AltGr as a plain KEY_RIGHTALT press with
/// no synthetic control event, so no pairing logic exists here.
/// </remarks>
public sealed class LinuxBrokerEventAdapter
{
    /// <summary>Ctrl/alt/meta are the shortcut modifiers, matching Windows.</summary>
    private const ModifierState ShortcutModifierMask =
        ModifierState.LeftControl
        | ModifierState.RightControl
        | ModifierState.LeftAlt
        | ModifierState.RightAlt
        | ModifierState.LeftMeta
        | ModifierState.RightMeta;

    private readonly TimeProvider _timeProvider;
    private readonly HashSet<PhysicalKeyId> _pressedKeys = new(capacity: 256);
    private ModifierState _pressedModifiers;

    public LinuxBrokerEventAdapter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Currently pressed modifier set; diagnostic mirror of the internal state.</summary>
    public ModifierState PressedModifiers => _pressedModifiers;

    /// <summary>Currently pressed keys; diagnostic mirror of the internal state.</summary>
    public IReadOnlyCollection<PhysicalKeyId> PressedKeys => _pressedKeys;

    /// <summary>
    /// Translates one decoded broker message. Non-event messages (Hello/HelloAck/
    /// Heartbeat/Error/Unknown and anything future) yield null; Reset always yields an
    /// input event; keyboard and pointer messages yield an event unless the payload is
    /// not representable (unknown button code, unknown evdev value).
    /// </summary>
    public InputEvent? Translate(BrokerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            BrokerKeyboardEvent keyboard => TranslateKeyboard(keyboard),
            BrokerPointerButtonEvent pointer => TranslatePointer(pointer),
            BrokerReset reset => TranslateReset(reset),
            _ => null,
        };
    }

    private InputEvent? TranslateKeyboard(BrokerKeyboardEvent message)
    {
        // evdev value semantics: 0 = release, 1 = press, 2 = auto-repeat. The broker
        // contract guarantees this set, but a corrupt or future frame must be tolerated:
        // unknown values are dropped without touching tracked state (never an error).
        KeyPhase phase;
        var isRepeat = false;
        switch (message.Value)
        {
            case BrokerProtocol.EvdevValueRelease:
                phase = KeyPhase.Release;
                break;
            case BrokerProtocol.EvdevValuePress:
                phase = KeyPhase.Press;
                break;
            case BrokerProtocol.EvdevValueRepeat:
                phase = KeyPhase.Press;
                isRepeat = true;
                break;
            default:
                return null;
        }

        // Unknown codes keep flowing under a stable neutral ID per the broker contract.
        var keyId = LinuxEvdevKeyMapper.TryGetKeyId(message.Code, out var mapped)
            ? mapped
            : LinuxEvdevKeyMapper.GetUnknownKeyId(message.Code);
        _ = LinuxEvdevKeyMapper.TryGetModifier(message.Code, out var modifier);

        if (!isRepeat)
        {
            if (phase == KeyPhase.Press)
            {
                _pressedModifiers |= modifier;
                _pressedKeys.Add(keyId);
            }
            else
            {
                _pressedModifiers &= ~modifier;
                _pressedKeys.Remove(keyId);
            }
        }

        // Repeats report the current pressed set but never change it.
        return InputEvent.FromKeyboard(new PhysicalKeyboardEvent(
            keyId,
            phase,
            isRepeat,
            _pressedModifiers,
            (_pressedModifiers & ShortcutModifierMask) != ModifierState.None,
            ToNeutralOrigin(message.Origin),
            unchecked((long)message.MonotonicTimestampNanoseconds),
            // Monotonic ns is not wall clock; mirror Windows, which stamps the event
            // with the time it is processed off its queue (TimeProvider.GetUtcNow).
            _timeProvider.GetUtcNow(),
            message.Sequence));
    }

    private InputEvent? TranslatePointer(BrokerPointerButtonEvent message)
    {
        // v1 restricts pointer codes to the BTN allow list; anything else is a contract
        // violation worth logging but not an error, so it is dropped.
        InputPointerButton button;
        switch (message.Code)
        {
            case BrokerProtocol.EvdevBtnLeft:
                button = InputPointerButton.Primary;
                break;
            case BrokerProtocol.EvdevBtnRight:
                button = InputPointerButton.Secondary;
                break;
            case BrokerProtocol.EvdevBtnMiddle:
                button = InputPointerButton.Middle;
                break;
            case BrokerProtocol.EvdevBtnSide:
                button = InputPointerButton.Side;
                break;
            case BrokerProtocol.EvdevBtnExtra:
                button = InputPointerButton.Extra;
                break;
            default:
                return null;
        }

        // No button repeat exists in v1; unknown values are dropped like keyboard values.
        if (message.Value is not (BrokerProtocol.EvdevValuePress or BrokerProtocol.EvdevValueRelease))
        {
            return null;
        }

        return InputEvent.FromPointer(new PhysicalPointerEvent(
            button,
            message.IsPress ? KeyPhase.Press : KeyPhase.Release,
            ToNeutralOrigin(message.Origin),
            unchecked((long)message.MonotonicTimestampNanoseconds),
            _timeProvider.GetUtcNow(),
            message.Sequence));
    }

    private InputEvent TranslateReset(BrokerReset message)
    {
        // Reset means the broker lost authoritative state (gap, device change, session
        // switch): clear everything so no modifier sticks on after a resume.
        _pressedModifiers = ModifierState.None;
        _pressedKeys.Clear();
        return InputEvent.Reset(message.Sequence);
    }

    private static InputOrigin ToNeutralOrigin(BrokerEventOrigin origin) => origin switch
    {
        BrokerEventOrigin.Hardware => InputOrigin.Hardware,
        BrokerEventOrigin.Synthetic => InputOrigin.Synthetic,
        _ => InputOrigin.Unknown,
    };
}
