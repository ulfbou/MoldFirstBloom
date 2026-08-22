using Mold.App.Timeline;
using Mold.Engine;
using Xunit;

namespace Mold.App.Tests;

public sealed class AnimationTimelineTests
{
    [Fact]
    public void ZeroIntermediateAndFullPrefixesPreserveAuthoritativeBatchCounts()
    {
        var (states, batches) = BuildHistory(3);
        for (var count = 0; count <= 3; count++)
        {
            var selection = AnimationTimeline.CreateSelection(states[count], count, count, batches.Take(count).ToArray());
            Assert.Equal(count, selection.AppliedActionCount);
            Assert.Equal(count, selection.Batches.Count);
            Assert.Equal(Enumerable.Range(1, count), selection.Batches.Select(item => item.ActionNumber));
        }
    }

    [Fact]
    public void DirectAndDerivedEventsKeepOrderTypeAndPayload()
    {
        GameEvent[] source =
        [
            new PiecePlacedEvent("Pair", [new Position(0, 0), new Position(1, 0)]),
            new GrowthResolvedEvent(new Position(1, 0), new Position(2, 0)),
            new BloomResolvedEvent([new Position(0, 0)], 1, 1, 0, 10),
            new GameEndedEvent("Garden Overgrown")
        ];
        var selection = AnimationTimeline.CreateSelection(new GameEngine().Create("A1B2C3D4"), 1, 1, [source]);
        var items = Assert.Single(selection.Batches).Events;
        Assert.Equal([TimelineEventKind.DirectPlacement, TimelineEventKind.DerivedGrowth, TimelineEventKind.DerivedBloom, TimelineEventKind.DerivedGameEnd], items.Select(item => item.Kind));
        Assert.Equal(source.Select(item => item.GetType()), items.Select(item => item.Event.GetType()));
        Assert.Equal([0, 1, 2, 3], items.Select(item => item.Order));
        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.Description)));
    }

    [Fact]
    public void SelectionSnapshotsStateEventCollectionsAndBatchMembership()
    {
        var state = new GameEngine().Create("A1B2C3D4");
        var cells = new List<Position> { new(0, 0) };
        var events = new List<GameEvent> { new PiecePlacedEvent("Spore", cells) };
        var batches = new List<IReadOnlyList<GameEvent>> { events };
        var selection = AnimationTimeline.CreateSelection(state, 1, 1, batches);
        cells.Add(new(1, 0));
        events.Add(new GameEndedEvent("changed"));
        batches.Clear();
        Assert.Single(selection.Batches);
        var placed = Assert.IsType<PiecePlacedEvent>(Assert.Single(selection.Batches[0].Events).Event);
        Assert.Single(placed.Cells);
        Assert.NotSame(state.Cells, selection.State.Cells);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAndReducedMotionProduceSameReadableSequence(bool reducedMotion)
    {
        var (_, batches) = BuildHistory(2);
        var selection = AnimationTimeline.CreateSelection(new GameEngine().Create("A1B2C3D4"), 2, 2, batches.Take(2).ToArray());
        var delay = new RecordingDelay();
        var player = new AnimationTimeline(delay);
        var presented = new List<string>();
        await player.PlayAsync(selection, reducedMotion, frame => { presented.Add(frame.Item.Description); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(selection.Batches.SelectMany(batch => batch.Events).Select(item => item.Description), presented);
        Assert.Equal(reducedMotion ? 0 : presented.Count, delay.Durations.Count);
    }

    [Fact]
    public async Task PauseAndResumeDoNotChangeSemanticOutput()
    {
        var (_, batches) = BuildHistory(1);
        var selection = AnimationTimeline.CreateSelection(new GameEngine().Create("A1B2C3D4"), 1, 1, batches.Take(1).ToArray());
        var player = new AnimationTimeline(new RecordingDelay());
        player.Pause();
        var presented = new List<GameEvent>();
        var play = player.PlayAsync(selection, true, frame => { presented.Add(frame.Item.Event); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.False(play.IsCompleted);
        player.Resume();
        await play;
        Assert.Equal(selection.Batches[0].Events.Select(item => item.Event.GetType()), presented.Select(item => item.GetType()));
    }

    [Fact]
    public async Task CancellationInterruptsWithoutMutatingSelection()
    {
        var (_, batches) = BuildHistory(2);
        var selection = AnimationTimeline.CreateSelection(new GameEngine().Create("A1B2C3D4"), 2, 2, batches.Take(2).ToArray());
        var before = selection.Batches.SelectMany(batch => batch.Events).Select(item => item.Description).ToArray();
        using var cancellation = new CancellationTokenSource();
        var player = new AnimationTimeline(new CancelingDelay(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.PlayAsync(selection, false, _ => Task.CompletedTask, cancellation.Token));
        Assert.Equal(before, selection.Batches.SelectMany(batch => batch.Events).Select(item => item.Description));
    }

    [Fact]
    public async Task FastForwardWakesActiveDelayAndPreservesAuthoritativeSelection()
    {
        var (_, batches) = BuildHistory(2);
        var selection = AnimationTimeline.CreateSelection(
            new GameEngine().Create("A1B2C3D4"), 2, 2, batches.Take(2).ToArray());
        var before = CaptureSelection(selection);
        var delay = new ControllableDelay();
        var player = new AnimationTimeline(delay);
        var presented = new List<string>();

        var playback = player.PlayAsync(selection, false, frame =>
        {
            presented.Add(frame.Item.Description);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        var first = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(120), first.Duration);
        player.FastForward();
        await first.Canceled;

        var accelerated = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(24), accelerated.Duration);
        accelerated.Complete();

        var remainingDelayCount = selection.Batches
            .SelectMany(batch => batch.Events)
            .Count() - 1;

        for (var index = 0; index < remainingDelayCount; index++)
        {
            (await delay.NextAsync()).Complete();
        }

        await playback.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            selection.Batches.SelectMany(batch => batch.Events).Select(item => item.Description),
            presented);
        Assert.Equal(before, CaptureSelection(selection));
    }

    [Fact]
    public async Task SkipWakesActiveDelayStopsPlaybackAndPreservesStaticMeaning()
    {
        var (_, batches) = BuildHistory(2);
        var selection = AnimationTimeline.CreateSelection(
            new GameEngine().Create("A1B2C3D4"), 2, 2, batches.Take(2).ToArray());
        var before = CaptureSelection(selection);
        var readableMeaning = selection.Batches
            .SelectMany(batch => batch.Events)
            .Select(item => item.Description)
            .ToArray();
        var delay = new ControllableDelay();
        var player = new AnimationTimeline(delay);
        var presented = new List<string>();

        var playback = player.PlayAsync(selection, false, frame =>
        {
            presented.Add(frame.Item.Description);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        var active = await delay.NextAsync();
        player.Skip();
        await active.Canceled;
        await playback;

        Assert.Single(presented);
        Assert.Equal(before, CaptureSelection(selection));
        Assert.Equal(readableMeaning, selection.Batches
            .SelectMany(batch => batch.Events)
            .Select(item => item.Description));
        Assert.False(delay.HasPendingRequest);
    }

    private static string CaptureSelection(TimelineSelection selection) =>
        $"{selection.RequestedActionCount}|{selection.AppliedActionCount}|" +
        $"{selection.State.Turn}|{selection.State.Score}|" +
        string.Join("|", selection.Batches.Select(batch =>
            $"{batch.ActionNumber}:" + string.Join(",", batch.Events.Select(item =>
                $"{item.Order}:{item.Kind}:{item.Event}:{item.Description}"))));

    private static (IReadOnlyList<GameState> States, IReadOnlyList<IReadOnlyList<GameEvent>> Batches) BuildHistory(int count)
    {
        var engine = new GameEngine();
        var state = engine.Create("A1B2C3D4");
        var states = new List<GameState> { state };
        var batches = new List<IReadOnlyList<GameEvent>>();
        for (var action = 0; action < count; action++)
        {
            var command = FindFirstValid(engine, state);
            var result = engine.Execute(state, command);
            Assert.True(result.Succeeded);
            state = result.State;
            states.Add(state);
            batches.Add(result.Events);
        }
        return (states, batches);
    }

    private static PlacePieceCommand FindFirstValid(GameEngine engine, GameState state)
    {
        for (var slot = 0; slot < state.Hand.Count; slot++)
        for (var rotation = 0; rotation < 4; rotation++)
        for (var y = 0; y < state.Height; y++)
        for (var x = 0; x < state.Width; x++)
        {
            var command = new PlacePieceCommand(slot, new Position(x, y), rotation);
            if (engine.Preview(state, command).IsValid) return command;
        }
        throw new InvalidOperationException("No valid command found.");
    }

    private sealed class RecordingDelay : ITimelineDelay
    {
        public List<TimeSpan> Durations { get; } = [];
        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) { Durations.Add(duration); return Task.CompletedTask; }
    }

    private sealed class CancelingDelay(CancellationTokenSource source) : ITimelineDelay
    {
        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) { source.Cancel(); return Task.FromCanceled(cancellationToken); }
    }

    private sealed class ControllableDelay : ITimelineDelay
    {
        private readonly Queue<DelayRequest> _requests = new();
        private TaskCompletionSource<DelayRequest> _next = NewNext();

        public bool HasPendingRequest
        {
            get { lock (_requests) return _requests.Count > 0; }
        }

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(duration, cancellationToken);
            TaskCompletionSource<DelayRequest> next;
            lock (_requests)
            {
                _requests.Enqueue(request);
                next = _next;
                _next = NewNext();
            }
            next.TrySetResult(request);
            return request.Task;
        }

        public async Task<DelayRequest> NextAsync()
        {
            Task<DelayRequest> wait;

            lock (_requests)
            {
                if (_requests.TryDequeue(out var queued))
                {
                    return queued;
                }

                wait = _next.Task;
            }

            var request = await wait.WaitAsync(
                TestContext.Current.CancellationToken);

            lock (_requests)
            {
                if (_requests.Count > 0 &&
                    ReferenceEquals(_requests.Peek(), request))
                {
                    _requests.Dequeue();
                }
            }

            return request;
        }

        private static TaskCompletionSource<DelayRequest> NewNext() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelayRequest
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public DelayRequest(TimeSpan duration, CancellationToken cancellationToken)
        {
            Duration = duration;
            _registration = cancellationToken.Register(() =>
            {
                _canceled.TrySetResult();
                _completion.TrySetCanceled(cancellationToken);
            });
        }

        public TimeSpan Duration { get; }
        public Task Task => _completion.Task;
        public Task Canceled => _canceled.Task;

        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }
}
