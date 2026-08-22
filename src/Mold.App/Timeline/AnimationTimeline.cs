using Mold.App.Services;
using Mold.Engine;

namespace Mold.App.Timeline;

public enum TimelineEventKind
{
    DirectPlacement,
    DerivedGrowth,
    DerivedBloom,
    DerivedGameEnd
}

public sealed record TimelineEventItem(
    int Order,
    TimelineEventKind Kind,
    GameEvent Event,
    string Label,
    string Description,
    IReadOnlyList<BoardEffect> Effects);

public sealed record TimelineBatch(int ActionNumber, IReadOnlyList<TimelineEventItem> Events);

public sealed record TimelineSelection(
    GameState State,
    int RequestedActionCount,
    int AppliedActionCount,
    IReadOnlyList<TimelineBatch> Batches);

public sealed record TimelinePlaybackFrame(
    int BatchIndex,
    int EventIndex,
    TimelineEventItem Item,
    bool IsFinal);

public interface ITimelineDelay
{
    Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class SystemTimelineDelay : ITimelineDelay
{
    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}

public sealed class AnimationTimeline(ITimelineDelay delay)
{
    private readonly object _gate = new();
    private TaskCompletionSource _controlSignal = NewSignal();
    private bool _paused;
    private bool _skip;
    private bool _fastForward;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            PulseControlLocked();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            PulseControlLocked();
        }
    }

    public void FastForward()
    {
        lock (_gate)
        {
            if (_fastForward) return;
            _fastForward = true;
            PulseControlLocked();
        }
    }

    public void Skip()
    {
        lock (_gate)
        {
            _skip = true;
            _paused = false;
            PulseControlLocked();
        }
    }

    public async Task PlayAsync(
        TimelineSelection selection,
        bool reducedMotion,
        Func<TimelinePlaybackFrame, Task> present,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(present);
        lock (_gate) { _skip = false; _fastForward = false; }

        var items = selection.Batches
            .SelectMany((batch, batchIndex) => batch.Events.Select((item, eventIndex) =>
                (batchIndex, eventIndex, item)))
            .ToArray();

        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(cancellationToken);
            bool skip;
            lock (_gate) skip = _skip;
            if (skip) break;

            var current = items[index];
            await present(new(
                current.batchIndex,
                current.eventIndex,
                current.item,
                index == items.Length - 1));

            var duration = reducedMotion ? TimeSpan.Zero : DurationFor(current.item.Kind);
            if (duration > TimeSpan.Zero &&
                !await WaitForPresentationDelayAsync(duration, cancellationToken))
            {
                break;
            }
        }
    }

    public static TimelineSelection CreateSelection(
        GameState state,
        int requestedActionCount,
        int appliedActionCount,
        IReadOnlyList<IReadOnlyList<GameEvent>> eventBatches)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(eventBatches);

        var batches = eventBatches.Select((batch, batchIndex) => new TimelineBatch(
            batchIndex + 1,
            batch.Select((gameEvent, eventIndex) => CreateItem(eventIndex, gameEvent)).ToArray()
        )).ToArray();

        return new(CloneState(state), requestedActionCount, appliedActionCount, batches);
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? wait;
            lock (_gate)
            {
                if (!_paused || _skip) return;
                wait = _controlSignal.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private async Task<bool> WaitForPresentationDelayAsync(
        TimeSpan normalDuration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task control;
            bool skip;
            bool fastForward;
            lock (_gate)
            {
                skip = _skip;
                fastForward = _fastForward;
                control = _controlSignal.Task;
            }

            if (skip) return false;

            var duration = fastForward
                ? TimeSpan.FromTicks(normalDuration.Ticks / 5)
                : normalDuration;
            using var activeDelay =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = delay.WaitAsync(duration, activeDelay.Token);
            var completed = await Task.WhenAny(delayTask, control);

            if (completed == delayTask)
            {
                await delayTask;
                return true;
            }

            await activeDelay.CancelAsync();
            try
            {
                await delayTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(cancellationToken);
        }
    }

    private void PulseControlLocked()
    {
        var signal = _controlSignal;
        _controlSignal = NewSignal();
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimelineEventItem CreateItem(int order, GameEvent gameEvent) => gameEvent switch
    {
        PiecePlacedEvent placed => new(
            order, TimelineEventKind.DirectPlacement, Clone(placed), "Direct placement",
            $"Placed {placed.PieceName} at {string.Join(", ", placed.Cells.Select(Label))}.",
            placed.Cells.Select(position => new BoardEffect("place", position)).ToArray()),
        GrowthResolvedEvent growth => new(
            order, TimelineEventKind.DerivedGrowth, growth with { }, "Derived growth",
            $"Growth resolved from {Label(growth.GrowthFrom)} to {Label(growth.GrowthTo)}.",
            [new BoardEffect("grow", growth.GrowthTo, "+")]),
        BloomResolvedEvent bloom => new(
            order, TimelineEventKind.DerivedBloom, Clone(bloom), "Derived bloom",
            $"Bloom resolved at {string.Join(", ", bloom.Cells.Select(Label))}: {bloom.CellCount} cells, {bloom.PieceCount} piece, {bloom.EnclosureCount} enclosures, {bloom.Score} points.",
            bloom.Cells.Select(position => new BoardEffect("bloom", position, $"+{bloom.Score}")).ToArray()),
        GameEndedEvent ended => new(
            order, TimelineEventKind.DerivedGameEnd, ended with { }, "Derived game end",
            $"Game ended: {ended.Verdict}.", []),
        _ => throw new NotSupportedException($"Unsupported authoritative event type: {gameEvent.GetType().FullName}")
    };

    private static PiecePlacedEvent Clone(PiecePlacedEvent item) =>
        item with { Cells = item.Cells.ToArray() };

    private static BloomResolvedEvent Clone(BloomResolvedEvent item) =>
        item with { Cells = item.Cells.ToArray() };

    private static GameState CloneState(GameState state) => state with
    {
        Cells = state.Cells.ToArray(),
        Hand = state.Hand.ToArray()
    };

    private static TimeSpan DurationFor(TimelineEventKind kind) => kind switch
    {
        TimelineEventKind.DirectPlacement => TimeSpan.FromMilliseconds(120),
        TimelineEventKind.DerivedGrowth => TimeSpan.FromMilliseconds(230),
        TimelineEventKind.DerivedBloom => TimeSpan.FromMilliseconds(400),
        TimelineEventKind.DerivedGameEnd => TimeSpan.FromMilliseconds(150),
        _ => TimeSpan.Zero
    };

    private static string Label(Position position) => $"{(char)('A' + position.X)}{position.Y + 1}";
}
