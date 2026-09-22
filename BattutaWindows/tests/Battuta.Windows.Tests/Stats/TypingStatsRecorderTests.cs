using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.Persistence;
using Battuta.Windows.Stats.Services;
using Battuta.Windows.Input;

namespace Battuta.Windows.Tests.Stats;

public sealed class TypingStatsRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 23, 59, 59, TimeSpan.Zero);
    private static readonly TypingApplicationIdentity App = new(
        "win32:test",
        "Test App",
        "Test.exe");

    [Fact]
    public async Task PreservesCharacterRepeatAndPhysicalPressSemantics()
    {
        var persistence = new CapturingPersistence();
        await using var recorder = CreateRecorder(persistence);

        recorder.RecordKeyDown(PhysicalKeys.KeyA, false, false, App, Now);
        recorder.RecordKeyDown(PhysicalKeys.KeyA, true, false, App, Now);
        recorder.RecordKeyDown(PhysicalKeys.LeftShift, false, false, App, Now);
        recorder.RecordKeyDown(PhysicalKeys.KeyC, false, true, App, Now);
        recorder.RecordKeyDown(PhysicalKeys.Enter, false, false, App, Now);
        var unknown = new PhysicalKeyId("win.scan.e0.005E");
        recorder.RecordKeyDown(unknown, false, false, App, Now);

        Assert.True(await recorder.FlushPendingAsync());
        var batch = Assert.Single(persistence.SuccessfulBatches);
        Assert.Equal(2, batch.CharacterAggregates.Sum(item => item.Count));
        Assert.Equal(2, Assert.Single(
            batch.CharacterAggregates,
            item => item.Application == App && item.SecondStartUtc == Now.ToUnixTimeSeconds()).Count);
        // Both A events aggregate into one second/application value.
        Assert.Equal(2, batch.CharacterAggregates[0].Count);
        Assert.Equal(5, batch.KeyAggregates.Sum(item => item.Count));
        Assert.Equal(1, Assert.Single(batch.KeyAggregates, item => item.PhysicalKeyId == unknown).Count);
        Assert.Equal(
            1,
            Assert.Single(batch.KeyAggregates, item => item.PhysicalKeyId == PhysicalKeys.KeyA).Count);
    }

    [Fact]
    public async Task FailedMaterializedBatchRetriesWithoutLosingLiveInput()
    {
        var persistence = new CapturingPersistence(failuresBeforeSuccess: 1);
        await using var recorder = CreateRecorder(persistence);
        for (var index = 0; index < 100; index++)
        {
            recorder.RecordKeyDown(PhysicalKeys.KeyA, false, false, App, Now);
            recorder.RecordMouseDown(Now);
        }

        Assert.False(await recorder.FlushPendingAsync());
        recorder.RecordKeyDown(PhysicalKeys.KeyB, false, false, App, Now.AddSeconds(1));
        recorder.RecordMouseDown(Now.AddSeconds(1));
        Assert.True(await recorder.FlushPendingAsync());

        Assert.Equal(3, persistence.Attempts);
        Assert.Equal(
            101,
            persistence.SuccessfulBatches
                .SelectMany(batch => batch.CharacterAggregates)
                .Sum(item => item.Count));
        Assert.Equal(
            101,
            persistence.SuccessfulBatches
                .SelectMany(batch => batch.KeyAggregates)
                .Sum(item => item.Count));
        Assert.Equal(
            101,
            persistence.SuccessfulBatches
                .SelectMany(batch => batch.MouseAggregates)
                .Sum(item => item.Count));
    }

    [Fact]
    public async Task SuspendsAfterSixFailuresAndRecoversFrozenBatch()
    {
        var persistence = new CapturingPersistence(failuresBeforeSuccess: 6);
        await using var recorder = CreateRecorder(persistence);
        recorder.RecordKeyDown(PhysicalKeys.KeyA, false, false, App, Now);
        recorder.RecordMouseDown(Now);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            Assert.False(await recorder.FlushPendingAsync());
        }

        Assert.True(recorder.IsRecordingSuspended);
        recorder.RecordKeyDown(PhysicalKeys.KeyB, false, false, App, Now.AddSeconds(1));
        recorder.RecordMouseDown(Now.AddSeconds(1));
        Assert.True(await recorder.FlushPendingAsync());
        Assert.False(recorder.IsRecordingSuspended);

        recorder.RecordKeyDown(PhysicalKeys.KeyC, false, false, App, Now.AddSeconds(2));
        recorder.RecordMouseDown(Now.AddSeconds(2));
        Assert.True(await recorder.FlushPendingAsync());
        Assert.Equal(
            2,
            persistence.SuccessfulBatches
                .SelectMany(batch => batch.CharacterAggregates)
                .Sum(item => item.Count));
        Assert.Equal(
            2,
            persistence.SuccessfulBatches
                .SelectMany(batch => batch.MouseAggregates)
                .Sum(item => item.Count));
    }

    [Fact]
    public async Task MouseOnlyBatchesUseLocalDateAtEventTimeAcrossMidnight()
    {
        var persistence = new CapturingPersistence();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Stats UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        await using var recorder = new TypingStatsRecorder(persistence, zone, TimeSpan.FromHours(1));
        var beforeLocalMidnight = new DateTimeOffset(2026, 8, 24, 15, 59, 59, TimeSpan.Zero);
        recorder.RecordMouseDown(beforeLocalMidnight);
        recorder.RecordMouseDown(beforeLocalMidnight.AddSeconds(1));
        recorder.RecordMouseDown(beforeLocalMidnight.AddSeconds(2));

        Assert.True(await recorder.FlushPendingAsync());
        var batch = Assert.Single(persistence.SuccessfulBatches);
        Assert.Empty(batch.CharacterAggregates);
        Assert.Empty(batch.KeyAggregates);
        Assert.Equal(
            [new MouseClickAggregate(new DateOnly(2026, 8, 24), 1), new MouseClickAggregate(new DateOnly(2026, 8, 25), 2)],
            batch.MouseAggregates);
    }

    [Fact]
    public async Task ClearDiscardsFailedMouseBatchAndPendingInput()
    {
        var persistence = new CapturingPersistence(failuresBeforeSuccess: 2);
        await using var recorder = CreateRecorder(persistence);
        recorder.RecordMouseDown(Now);
        Assert.False(await recorder.FlushPendingAsync());
        recorder.RecordMouseDown(Now.AddSeconds(1));

        await recorder.ClearAllAsync();
        Assert.True(await recorder.FlushPendingAsync());
        Assert.Empty(persistence.SuccessfulBatches);
        Assert.Null(recorder.LastWriteError);

        recorder.RecordMouseDown(Now.AddSeconds(2));
        Assert.True(await recorder.FlushPendingAsync());
        Assert.Equal(1, Assert.Single(Assert.Single(persistence.SuccessfulBatches).MouseAggregates).Count);
    }

    [Fact]
    public async Task InputSinkCountsEachHardwareMouseButtonPressAndHonorsStatisticsToggle()
    {
        var persistence = new CapturingPersistence();
        await using var recorder = CreateRecorder(persistence);
        var enabled = true;
        var sink = new TypingStatsInputEventSink(recorder, () => enabled);

        async Task Send(WindowsPointerButton button, KeyPhase phase, WindowsInputOrigin origin) =>
            await sink.OnInputAsync(
                WindowsInputEvent.FromMouse(
                    new WindowsPointerInputEvent(button, phase, origin, Now, 1),
                    ForegroundApplicationSnapshot.Unknown),
                CancellationToken.None);

        foreach (var button in Enum.GetValues<WindowsPointerButton>())
        {
            await Send(button, KeyPhase.Press, WindowsInputOrigin.Hardware);
            await Send(button, KeyPhase.Release, WindowsInputOrigin.Hardware);
            await Send(button, KeyPhase.Press, WindowsInputOrigin.Injected);
            await Send(button, KeyPhase.Press, WindowsInputOrigin.LowerIntegrityInjected);
        }

        await Send((WindowsPointerButton)99, KeyPhase.Press, WindowsInputOrigin.Hardware);
        enabled = false;
        await Send(WindowsPointerButton.Primary, KeyPhase.Press, WindowsInputOrigin.Hardware);
        Assert.True(await recorder.FlushPendingAsync());

        var batch = Assert.Single(persistence.SuccessfulBatches);
        Assert.Equal(5, Assert.Single(batch.MouseAggregates).Count);
        Assert.Empty(batch.CharacterAggregates);
        Assert.Empty(batch.KeyAggregates);

        enabled = true;
        await Send(WindowsPointerButton.Primary, KeyPhase.Press, WindowsInputOrigin.Hardware);
        Assert.True(await recorder.FlushPendingAsync());
        Assert.Equal(6, persistence.SuccessfulBatches.SelectMany(item => item.MouseAggregates).Sum(item => item.Count));
    }

    [Fact]
    public async Task ComputesLocalDateAndHourAtEventTimeAcrossMidnight()
    {
        var persistence = new CapturingPersistence();
        await using var recorder = CreateRecorder(persistence);
        recorder.RecordKeyDown(PhysicalKeys.KeyA, false, false, App, Now);
        recorder.RecordKeyDown(PhysicalKeys.KeyB, false, false, App, Now.AddSeconds(1));
        Assert.True(await recorder.FlushPendingAsync());

        var batch = Assert.Single(persistence.SuccessfulBatches);
        Assert.Equal(
            [new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25)],
            batch.CharacterAggregates.Select(item => item.LocalDate).Distinct().Order().ToArray());
        Assert.Equal([23, 0], batch.CharacterAggregates.Select(item => item.LocalHour).ToArray());
        Assert.Equal(2, batch.KeyAggregates.Select(item => item.LocalDate).Distinct().Count());
    }

    [Fact]
    public async Task InputSinkUsesNormalizedShortcutFlagAndForegroundSnapshot()
    {
        var persistence = new CapturingPersistence();
        await using var recorder = CreateRecorder(persistence);
        var sink = new TypingStatsInputEventSink(recorder, () => true);
        var physicalKey = new WindowsPhysicalKey(
            PhysicalKeys.KeyA,
            KeyboardRowId.R2,
            null,
            true,
            true);
        var keyboard = new WindowsKeyboardInputEvent(
            physicalKey,
            KeyPhase.Press,
            false,
            ModifierState.LeftControl,
            true,
            WindowsInputOrigin.Hardware,
            Now,
            1);
        await sink.OnInputAsync(
            WindowsInputEvent.FromKeyboard(
                keyboard,
                new ForegroundApplicationSnapshot(42, "win32:editor", "Editor", "editor.exe")),
            CancellationToken.None);
        Assert.True(await recorder.FlushPendingAsync());

        var batch = Assert.Single(persistence.SuccessfulBatches);
        Assert.Empty(batch.CharacterAggregates);
        var physical = Assert.Single(batch.KeyAggregates);
        Assert.Equal(PhysicalKeys.KeyA, physical.PhysicalKeyId);
    }

    [Theory]
    [InlineData("KeyA", false, true)]
    [InlineData("KeyA", true, false)]
    [InlineData("Space", false, true)]
    [InlineData("Enter", false, false)]
    [InlineData("LeftShift", false, false)]
    [InlineData("Eisu", false, false)]
    public void CharacterAllowListIsPhysicalAndShortcutAware(
        string stableId,
        bool isShortcutModified,
        bool expected)
    {
        Assert.Equal(
            expected,
            TypingCharacterKeyFilter.CountsAsCharacter(
                new PhysicalKeyId(stableId),
                isShortcutModified));
    }

    private static TypingStatsRecorder CreateRecorder(ITypingStatsPersistence persistence) =>
        new(persistence, TimeZoneInfo.Utc, TimeSpan.FromHours(1));

    private sealed class CapturingPersistence(int failuresBeforeSuccess = 0)
        : ITypingStatsPersistence
    {
        private readonly object _sync = new();
        private int _attempts;

        public int Attempts
        {
            get
            {
                lock (_sync)
                {
                    return _attempts;
                }
            }
        }

        public List<TypingStatsWriteBatch> SuccessfulBatches { get; } = [];

        public Task RecordAsync(
            TypingStatsWriteBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _attempts++;
                if (_attempts <= failuresBeforeSuccess)
                {
                    throw new TypingStatsStoreException(
                        TypingStatsStoreErrorKind.Busy,
                        "busy");
                }

                SuccessfulBatches.Add(batch);
            }

            return Task.CompletedTask;
        }

        public Task<TypingStatsSnapshot> LoadSnapshotAsync(
            TypingTimelineRange timelineRange = TypingTimelineRange.OneHour,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TypingRangeReportSnapshot> LoadReportAsync(
            TypingDateRange range,
            TypingDateRange? comparisonRange = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAllAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                SuccessfulBatches.Clear();
            }

            return Task.CompletedTask;
        }
    }
}
