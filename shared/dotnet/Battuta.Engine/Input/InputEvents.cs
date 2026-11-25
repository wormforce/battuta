using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;

namespace Battuta.Engine.Input;

public enum InputEventKind
{
    Keyboard,
    Pointer,
    Reset,
}

public enum InputOrigin
{
    Hardware,
    Synthetic,
    Unknown,
}

/// <summary>
/// Neutral pointer buttons. Windows maps X1/X2 to Side/Extra; Linux maps
/// BTN_LEFT/RIGHT/MIDDLE/SIDE/EXTRA directly.
/// </summary>
public enum InputPointerButton
{
    Primary,
    Secondary,
    Middle,
    Side,
    Extra,
}

public readonly record struct PhysicalKeyboardEvent(
    PhysicalKeyId Key,
    KeyPhase Phase,
    bool IsRepeat,
    ModifierState Modifiers,
    bool IsShortcutModified,
    InputOrigin Origin,
    long MonotonicTimestampNanoseconds,
    DateTimeOffset Timestamp,
    ulong Sequence);

public readonly record struct PhysicalPointerEvent(
    InputPointerButton Button,
    KeyPhase Phase,
    InputOrigin Origin,
    long MonotonicTimestampNanoseconds,
    DateTimeOffset Timestamp,
    ulong Sequence);

/// <summary>
/// The single business-layer input event. Platforms adapt their raw event
/// streams into this type; the runtime, audio, and statistics layers consume
/// only this type. Application carries the resolved foreground application
/// identity (or Unknown) so per-platform foreground providers stay behind the
/// adapter boundary.
/// </summary>
public readonly record struct InputEvent
{
    private InputEvent(
        InputEventKind kind,
        PhysicalKeyboardEvent keyboard,
        PhysicalPointerEvent pointer,
        TypingApplicationIdentity? application)
    {
        Kind = kind;
        Keyboard = keyboard;
        Pointer = pointer;
        Application = application;
    }

    public InputEventKind Kind { get; }

    public PhysicalKeyboardEvent Keyboard { get; }

    public PhysicalPointerEvent Pointer { get; }

    public TypingApplicationIdentity? Application { get; }

    public static InputEvent FromKeyboard(
        PhysicalKeyboardEvent value,
        TypingApplicationIdentity? application = null) =>
        new(InputEventKind.Keyboard, value, default, application);

    public static InputEvent FromPointer(
        PhysicalPointerEvent value,
        TypingApplicationIdentity? application = null) =>
        new(InputEventKind.Pointer, default, value, application);

    public static InputEvent Reset(ulong sequence) =>
        new(InputEventKind.Reset, default, default, null);
}

public interface IInputEventSink
{
    ValueTask OnInputAsync(InputEvent inputEvent, CancellationToken cancellationToken);
}

public sealed class DelegateInputEventSink(
    Func<InputEvent, CancellationToken, ValueTask> handler) : IInputEventSink
{
    private readonly Func<InputEvent, CancellationToken, ValueTask> _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask OnInputAsync(
        InputEvent inputEvent,
        CancellationToken cancellationToken) =>
        _handler(inputEvent, cancellationToken);
}

/// <summary>
/// A live input source already bound to its sink. Windows binds the Win32
/// hook service in its composition root; Linux binds the input Broker client.
/// Sources must emit InputEvent.Reset and clear pressed state on sequence
/// gaps, device removal, reconnects, and session switches.
/// </summary>
public interface IInputSource : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
