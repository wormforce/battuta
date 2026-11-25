using Battuta.Core.Input;
using Battuta.Engine.Input;
using Battuta.Windows.Stats.Models;

namespace Battuta.Windows.Stats.Services;

/// <summary>
/// Routes normalized key-down events into the statistics recorder. Statistics
/// count allow-listed physical character keys, include auto-repeat, and
/// exclude shortcut-modified presses; the heatmap allow-list lives in
/// TypingCharacterKeyFilter.
/// </summary>
public sealed class TypingStatsInputEventSink(
    TypingStatsRecorder recorder,
    Func<bool> isEnabled) : IInputEventSink
{
    private readonly TypingStatsRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly Func<bool> _isEnabled =
        isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));

    public ValueTask OnInputAsync(
        InputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isEnabled()
            || inputEvent.Kind != InputEventKind.Keyboard
            || inputEvent.Keyboard.Phase != KeyPhase.Press)
        {
            return ValueTask.CompletedTask;
        }

        var application = inputEvent.Application ?? TypingApplicationIdentity.Unknown;
        var keyboard = inputEvent.Keyboard;
        _recorder.RecordKeyDown(
            keyboard.Key,
            keyboard.IsRepeat,
            keyboard.IsShortcutModified,
            application,
            keyboard.Timestamp);
        return ValueTask.CompletedTask;
    }
}
