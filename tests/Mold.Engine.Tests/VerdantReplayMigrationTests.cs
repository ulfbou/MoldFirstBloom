using Mold.Engine;
using Verdant.Replay;
using Xunit;

namespace Mold.Engine.Tests;

public sealed class VerdantReplayMigrationTests
{
    private const string Seed = "A1B2C3D4";

    private readonly GameEngine _engine = new();

    [Fact]
    public void ReplayZeroReconstructsCanonicalInitialState()
    {
        var log = BuildAcceptedLog(3);
        var expected = _engine.Create(Seed);
        var replay = new FirstBloomReplay(_engine);

        var actual = AssertSuccess(
            replay.Run(Seed, log, 0));

        AssertStateEqual(expected, actual.State);
        Assert.Empty(actual.Events);
        Assert.Empty(actual.EventBatches);
        Assert.Equal(0, actual.AppliedActionCount);
    }

    [Fact]
    public void EveryValidPrefixMatchesLegacyStateAndEvents()
    {
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);

        for (var actionCount = 0;
             actionCount <= log.Count;
             actionCount++)
        {
            var expected = Replay.Run(
                _engine,
                Seed,
                log,
                actionCount);

            var actual = AssertSuccess(
                replay.Run(
                    Seed,
                    log,
                    actionCount));

            AssertStateEqual(expected.State, actual.State);
            AssertEventSequenceEqual(expected.Events, actual.Events);
            Assert.Equal(actionCount, actual.AppliedActionCount);
        }
    }

    [Fact]
    public void EveryPrefixReconstructsFromFreshInitialization()
    {
        var log = BuildAcceptedLog(3);
        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        for (var actionCount = 0;
             actionCount <= log.Count;
             actionCount++)
        {
            var result = AssertSuccess(
                replay.Run(
                    Seed,
                    log,
                    actionCount));

            Assert.Equal(actionCount, result.AppliedActionCount);
        }

        Assert.Equal(log.Count + 1, spy.CreateCallCount);

        Assert.Equal(
            Enumerable.Range(0, log.Count + 1).Sum(),
            spy.ExecuteCallCount);
    }

    [Fact]
    public void ReplayAppliesOnlyTheRequestedPrefix()
    {
        var log = BuildAcceptedLog(3);
        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        var result = AssertSuccess(
            replay.Run(Seed, log, 2));

        Assert.Equal(1, spy.CreateCallCount);
        Assert.Equal(2, spy.ExecuteCallCount);
        Assert.Equal(2, result.AppliedActionCount);
        Assert.Equal(2, result.State.Turn);
        Assert.Equal(2, result.EventBatches.Count);
    }

    [Fact]
    public void EventBatchesMatchDirectExecutionPerAction()
    {
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);
        var result = AssertSuccess(
            replay.Run(Seed, log, log.Count));

        var state = _engine.Create(Seed);

        Assert.Equal(log.Count, result.EventBatches.Count);

        for (var actionIndex = 0;
             actionIndex < log.Count;
             actionIndex++)
        {
            var entry = log[actionIndex];
            var direct = _engine.Execute(
                state,
                new PlacePieceCommand(
                    entry.Slot,
                    entry.Origin,
                    entry.Rotation));

            Assert.True(direct.Succeeded);

            AssertEventSequenceEqual(
                direct.Events,
                result.EventBatches[actionIndex]);

            state = direct.State;
        }

        AssertEventSequenceEqual(
            result.EventBatches.SelectMany(batch => batch).ToArray(),
            result.Events);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(999)]
    public void InvalidPrefixReturnsTypedFailureBeforeInitializationOrExecution(
        int actionCount)
    {
        var log = BuildAcceptedLog(3);
        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        var result = replay.Run(
            Seed,
            log,
            actionCount);

        var failure =
            Assert.IsType<
                ReplayResult<GameState, GameEvent>.Failure>(
                    result);

        Assert.Equal(
            ReplayErrorCode.InvalidReplayActionCount,
            failure.Error.Code);

        Assert.Null(failure.Error.ActionIndex);
        Assert.Null(failure.Error.AdapterFailureCode);
        Assert.Equal(0, spy.CreateCallCount);
        Assert.Equal(0, spy.ExecuteCallCount);
        Assert.Empty(spy.ExecutedCommands);
    }

    [Fact]
    public void RejectedAuthoritativeActionReturnsStableFailureAndStopsReplay()
    {
        var validLog = BuildAcceptedLog(2);
        var invalidEntry = new ActionEntry(
            99,
            new Position(0, 0),
            0,
            2);

        IReadOnlyList<ActionEntry> log =
        [
            validLog[0],
            invalidEntry,
            validLog[1]
        ];

        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        var result = replay.Run(
            Seed,
            log,
            log.Count);

        var failure =
            Assert.IsType<
                ReplayResult<GameState, GameEvent>.Failure>(
                    result);

        Assert.Equal(
            ReplayErrorCode.AuthoritativeCommandRejected,
            failure.Error.Code);

        Assert.Equal(1, failure.Error.ActionIndex);
        Assert.Equal(
            "INVALID_HAND_SLOT",
            failure.Error.AdapterFailureCode);

        Assert.Equal(1, spy.CreateCallCount);
        Assert.Equal(2, spy.ExecuteCallCount);
        Assert.Equal(2, spy.ExecutedCommands.Count);

        Assert.DoesNotContain(
            spy.ExecutedCommands,
            command =>
                command.Slot == validLog[1].Slot &&
                command.Origin == validLog[1].Origin &&
                command.Rotation == validLog[1].Rotation);
    }

    [Fact]
    public void AdapterMapsEveryCurrentCommandErrorToStableCode()
    {
        Assert.Equal(
            "GAME_ALREADY_ENDED",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.GameAlreadyEnded));

        Assert.Equal(
            "INVALID_HAND_SLOT",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.InvalidHandSlot));

        Assert.Equal(
            "OUTSIDE_BOARD",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.OutsideBoard));

        Assert.Equal(
            "OCCUPIED_CELL",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.OccupiedCell));

        Assert.Equal(
            "NO_CONNECTED_CELL",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.NoConnectedCell));

        Assert.Equal(
            "UNEXPECTED_SUCCESS_ERROR",
            FirstBloomReplayAdapter.ToStableFailureCode(
                CommandError.None));
    }

    [Fact]
    public void AdapterDelegatesAcceptedExecutionWithoutChangingItsResult()
    {
        var state = _engine.Create(Seed);
        var command = FindFirstValid(state);
        var direct = _engine.Execute(state, command);

        Assert.True(direct.Succeeded);

        var action = ToActionEntry(
            command,
            direct.State.Turn);

        var adapter =
            new FirstBloomReplayAdapter(_engine);

        var adapted = adapter.Execute(
            state,
            action);

        var accepted =
            Assert.IsType<
                ReplayStepResult<
                    GameState,
                    GameEvent>.Accepted>(
                        adapted);

        AssertStateEqual(direct.State, accepted.State);
        AssertEventSequenceEqual(
            direct.Events,
            accepted.Events);
    }

    [Fact]
    public void AdapterDelegatesCanonicalInitialization()
    {
        var spy = new SpyGameEngine(_engine);
        var adapter = new FirstBloomReplayAdapter(spy);

        var state = adapter.CreateInitialState(
            new FirstBloomReplayInitialization(Seed));

        Assert.Equal(1, spy.CreateCallCount);
        Assert.Equal([Seed], spy.CreatedSeeds);
        AssertStateEqual(_engine.Create(Seed), state);
    }

    [Fact]
    public void RepeatedReplayProducesEquivalentStateEventsAndBatches()
    {
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);

        var first = AssertSuccess(
            replay.Run(Seed, log));

        var second = AssertSuccess(
            replay.Run(Seed, log));

        var third = AssertSuccess(
            replay.Run(Seed, log));

        AssertStateEqual(first.State, second.State);
        AssertStateEqual(second.State, third.State);

        AssertEventSequenceEqual(first.Events, second.Events);
        AssertEventSequenceEqual(second.Events, third.Events);

        AssertEventBatchesEqual(
            first.EventBatches,
            second.EventBatches);

        AssertEventBatchesEqual(
            second.EventBatches,
            third.EventBatches);
    }

    [Fact]
    public void ReplayDoesNotMutateSuppliedActionLog()
    {
        var log = BuildAcceptedLog(3).ToList();
        var before = log.Select(CloneAction).ToArray();
        var replay = new FirstBloomReplay(_engine);

        _ = AssertSuccess(
            replay.Run(
                Seed,
                log,
                log.Count));

        Assert.Equal(before.Length, log.Count);

        for (var index = 0;
             index < before.Length;
             index++)
        {
            Assert.Equal(before[index], log[index]);
        }
    }

    [Fact]
    public void ReplayDoesNotMutatePreExistingLiveState()
    {
        var liveState = _engine.Create(Seed);
        var before = CaptureState(liveState);
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);

        _ = AssertSuccess(
            replay.Run(Seed, log));

        AssertStateSnapshotEqual(
            before,
            CaptureState(liveState));
    }

    [Fact]
    public void ReplayDoesNotMutatePreviouslyReturnedReplayState()
    {
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);

        var prefixOne = AssertSuccess(
            replay.Run(Seed, log, 1));

        var prefixOneBefore =
            CaptureState(prefixOne.State);

        _ = AssertSuccess(
            replay.Run(Seed, log, 3));

        AssertStateSnapshotEqual(
            prefixOneBefore,
            CaptureState(prefixOne.State));
    }

    [Fact]
    public void ReplayDoesNotMutatePreviouslyReturnedEventCollections()
    {
        var log = BuildAcceptedLog(3);
        var replay = new FirstBloomReplay(_engine);

        var first = AssertSuccess(
            replay.Run(Seed, log));

        var eventSnapshot =
            first.Events.Select(CaptureEvent).ToArray();

        var batchSnapshot =
            first.EventBatches
                .Select(batch =>
                    batch.Select(CaptureEvent).ToArray())
                .ToArray();

        _ = AssertSuccess(
            replay.Run(Seed, log));

        var currentEvents =
            first.Events.Select(CaptureEvent).ToArray();

        AssertEventSnapshotsEqual(
            eventSnapshot,
            currentEvents);

        Assert.Equal(
            batchSnapshot.Length,
            first.EventBatches.Count);

        for (var index = 0;
             index < batchSnapshot.Length;
             index++)
        {
            var currentBatch =
                first.EventBatches[index]
                    .Select(CaptureEvent)
                    .ToArray();

            AssertEventSnapshotsEqual(
                batchSnapshot[index],
                currentBatch);
        }
    }

    [Fact]
    public void ReplayInvokesOnlyInitializationAndCommandExecution()
    {
        var log = BuildAcceptedLog(3);
        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        _ = AssertSuccess(
            replay.Run(Seed, log));

        Assert.Equal(1, spy.CreateCallCount);
        Assert.Equal(log.Count, spy.ExecuteCallCount);
        Assert.Equal(0, spy.PreviewCallCount);
        Assert.Equal([Seed], spy.CreatedSeeds);
        Assert.Equal(log.Count, spy.ExecutedCommands.Count);
    }

    [Fact]
    public void ReplayHasNoStoragePresentationOrSessionDependency()
    {
        var constructor = typeof(FirstBloomReplay)
            .GetConstructors()
            .Single();

        Assert.Equal(
            [typeof(IGameEngine)],
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType));

        var adapterConstructor =
            typeof(FirstBloomReplayAdapter)
                .GetConstructors()
                .Single();

        Assert.Equal(
            [typeof(IGameEngine)],
            adapterConstructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType));

        var referencedAssemblies =
            typeof(FirstBloomReplay)
                .Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null)
                .ToArray();

        Assert.DoesNotContain(
            referencedAssemblies,
            name =>
                name!.Contains(
                    "Microsoft.JSInterop",
                    StringComparison.Ordinal));

        Assert.DoesNotContain(
            referencedAssemblies,
            name =>
                name!.Contains(
                    "Microsoft.AspNetCore.Components",
                    StringComparison.Ordinal));

        Assert.DoesNotContain(
            referencedAssemblies,
            name =>
                name!.Contains(
                    "Mold.App",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void ReplayDoesNotUsePreviewAsAnAlternativeReducer()
    {
        var log = BuildAcceptedLog(3);
        var spy = new SpyGameEngine(_engine);
        var replay = new FirstBloomReplay(spy);

        _ = AssertSuccess(
            replay.Run(Seed, log));

        Assert.Equal(0, spy.PreviewCallCount);
    }

    [Fact]
    public void StateParityCoversEveryCurrentAuthoritativeField()
    {
        var log = BuildAcceptedLog(3);
        var legacy = Replay.Run(
            _engine,
            Seed,
            log,
            log.Count);

        var migrated = AssertSuccess(
            new FirstBloomReplay(_engine)
                .Run(Seed, log));

        Assert.Equal(
            legacy.State.Width,
            migrated.State.Width);

        Assert.Equal(
            legacy.State.Height,
            migrated.State.Height);

        Assert.Equal(
            legacy.State.Cells,
            migrated.State.Cells);

        Assert.Equal(
            legacy.State.Hand,
            migrated.State.Hand);

        Assert.Equal(
            legacy.State.Score,
            migrated.State.Score);

        Assert.Equal(
            legacy.State.Turn,
            migrated.State.Turn);

        Assert.Equal(
            legacy.State.BloomCount,
            migrated.State.BloomCount);

        Assert.Equal(
            legacy.State.RandomState,
            migrated.State.RandomState);

        Assert.Equal(
            legacy.State.IsGameOver,
            migrated.State.IsGameOver);
    }

    [Fact]
    public void EventParityComparesConcreteTypesAndPayloads()
    {
        var log = BuildAcceptedLog(3);
        var legacy = Replay.Run(
            _engine,
            Seed,
            log,
            log.Count);

        var migrated = AssertSuccess(
            new FirstBloomReplay(_engine)
                .Run(Seed, log));

        AssertEventSequenceEqual(
            legacy.Events,
            migrated.Events);
    }

    private List<ActionEntry> BuildAcceptedLog(
        int actionCount)
    {
        var state = _engine.Create(Seed);
        var log = new List<ActionEntry>();

        for (var index = 0;
             index < actionCount;
             index++)
        {
            var command = FindFirstValid(state);
            var result = _engine.Execute(state, command);

            Assert.True(result.Succeeded);

            log.Add(
                ToActionEntry(
                    command,
                    result.State.Turn));

            state = result.State;
        }

        return log;
    }

    private PlacePieceCommand FindFirstValid(
        GameState state)
    {
        for (var slot = 0;
             slot < state.Hand.Count;
             slot++)
        {
            for (var rotation = 0;
                 rotation < 4;
                 rotation++)
            {
                for (var y = 0;
                     y < state.Height;
                     y++)
                {
                    for (var x = 0;
                         x < state.Width;
                         x++)
                    {
                        var command =
                            new PlacePieceCommand(
                                slot,
                                new Position(x, y),
                                rotation);

                        if (_engine
                            .Preview(state, command)
                            .IsValid)
                        {
                            return command;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "No valid command was found.");
    }

    private static ActionEntry ToActionEntry(
        PlacePieceCommand command,
        int turn) =>
        new(
            command.Slot,
            command.Origin,
            command.Rotation,
            turn);

    private static ActionEntry CloneAction(
        ActionEntry action) =>
        new(
            action.Slot,
            action.Origin,
            action.Rotation,
            action.Turn,
            action.RandomPosition);

    private static ReplayResult<
        GameState,
        GameEvent>.Success AssertSuccess(
            ReplayResult<
                GameState,
                GameEvent> result) =>
        Assert.IsType<
            ReplayResult<
                GameState,
                GameEvent>.Success>(
                    result);

    private static void AssertStateEqual(
        GameState expected,
        GameState actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Cells, actual.Cells);
        Assert.Equal(expected.Hand, actual.Hand);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.BloomCount, actual.BloomCount);
        Assert.Equal(expected.RandomState, actual.RandomState);
        Assert.Equal(expected.IsGameOver, actual.IsGameOver);
    }

    private static void AssertEventSequenceEqual(
        IReadOnlyList<GameEvent> expected,
        IReadOnlyList<GameEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (var index = 0;
             index < expected.Count;
             index++)
        {
            AssertEventEqual(
                expected[index],
                actual[index]);
        }
    }

    private static void AssertEventBatchesEqual(
        IReadOnlyList<IReadOnlyList<GameEvent>> expected,
        IReadOnlyList<IReadOnlyList<GameEvent>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (var index = 0;
             index < expected.Count;
             index++)
        {
            AssertEventSequenceEqual(
                expected[index],
                actual[index]);
        }
    }

    private static void AssertEventEqual(
        GameEvent expected,
        GameEvent actual)
    {
        Assert.Equal(
            expected.GetType(),
            actual.GetType());

        switch (expected)
        {
            case PiecePlacedEvent expectedPlaced:
            {
                var actualPlaced =
                    Assert.IsType<PiecePlacedEvent>(actual);

                Assert.Equal(
                    expectedPlaced.PieceName,
                    actualPlaced.PieceName);

                Assert.Equal(
                    expectedPlaced.Cells,
                    actualPlaced.Cells);

                break;
            }

            case GrowthResolvedEvent expectedGrowth:
            {
                var actualGrowth =
                    Assert.IsType<GrowthResolvedEvent>(actual);

                Assert.Equal(
                    expectedGrowth.GrowthFrom,
                    actualGrowth.GrowthFrom);

                Assert.Equal(
                    expectedGrowth.GrowthTo,
                    actualGrowth.GrowthTo);

                break;
            }

            case BloomResolvedEvent expectedBloom:
            {
                var actualBloom =
                    Assert.IsType<BloomResolvedEvent>(actual);

                Assert.Equal(
                    expectedBloom.Cells,
                    actualBloom.Cells);

                Assert.Equal(
                    expectedBloom.CellCount,
                    actualBloom.CellCount);

                Assert.Equal(
                    expectedBloom.PieceCount,
                    actualBloom.PieceCount);

                Assert.Equal(
                    expectedBloom.EnclosureCount,
                    actualBloom.EnclosureCount);

                Assert.Equal(
                    expectedBloom.Score,
                    actualBloom.Score);

                break;
            }

            case GameEndedEvent expectedEnded:
            {
                var actualEnded =
                    Assert.IsType<GameEndedEvent>(actual);

                Assert.Equal(
                    expectedEnded.Verdict,
                    actualEnded.Verdict);

                break;
            }

            default:
                throw new InvalidOperationException(
                    $"No structural event comparer exists for " +
                    $"{expected.GetType().FullName}.");
        }
    }

    private static StateSnapshot CaptureState(
        GameState state) =>
        new(
            state.Width,
            state.Height,
            state.Cells.ToArray(),
            state.Hand.ToArray(),
            state.Score,
            state.Turn,
            state.BloomCount,
            state.RandomState,
            state.IsGameOver);

    private static void AssertStateSnapshotEqual(
        StateSnapshot expected,
        StateSnapshot actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Cells, actual.Cells);
        Assert.Equal(expected.Hand, actual.Hand);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.BloomCount, actual.BloomCount);
        Assert.Equal(expected.RandomState, actual.RandomState);
        Assert.Equal(expected.IsGameOver, actual.IsGameOver);
    }

    private static EventSnapshot CaptureEvent(
        GameEvent item) =>
        item switch
        {
            PiecePlacedEvent placed =>
                new EventSnapshot(
                    nameof(PiecePlacedEvent),
                    placed.PieceName,
                    placed.Cells.ToArray(),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),

            GrowthResolvedEvent growth =>
                new EventSnapshot(
                    nameof(GrowthResolvedEvent),
                    null,
                    [],
                    growth.GrowthFrom,
                    growth.GrowthTo,
                    null,
                    null,
                    null,
                    null),

            BloomResolvedEvent bloom =>
                new EventSnapshot(
                    nameof(BloomResolvedEvent),
                    null,
                    bloom.Cells.ToArray(),
                    null,
                    null,
                    bloom.CellCount,
                    bloom.PieceCount,
                    bloom.EnclosureCount,
                    bloom.Score),

            GameEndedEvent ended =>
                new EventSnapshot(
                    nameof(GameEndedEvent),
                    ended.Verdict,
                    [],
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),

            _ =>
                throw new InvalidOperationException(
                    $"No event snapshot exists for " +
                    $"{item.GetType().FullName}.")
        };

    private static void AssertEventSnapshotsEqual(
        IReadOnlyList<EventSnapshot> expected,
        IReadOnlyList<EventSnapshot> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (var index = 0;
             index < expected.Count;
             index++)
        {
            var expectedEvent = expected[index];
            var actualEvent = actual[index];

            Assert.Equal(
                expectedEvent.Type,
                actualEvent.Type);

            Assert.Equal(
                expectedEvent.Text,
                actualEvent.Text);

            Assert.Equal(
                expectedEvent.Cells,
                actualEvent.Cells);

            Assert.Equal(
                expectedEvent.From,
                actualEvent.From);

            Assert.Equal(
                expectedEvent.To,
                actualEvent.To);

            Assert.Equal(
                expectedEvent.CellCount,
                actualEvent.CellCount);

            Assert.Equal(
                expectedEvent.PieceCount,
                actualEvent.PieceCount);

            Assert.Equal(
                expectedEvent.EnclosureCount,
                actualEvent.EnclosureCount);

            Assert.Equal(
                expectedEvent.Score,
                actualEvent.Score);
        }
    }

    private sealed record StateSnapshot(
        int Width,
        int Height,
        IReadOnlyList<CellMaterial> Cells,
        IReadOnlyList<HandPiece> Hand,
        int Score,
        int Turn,
        int BloomCount,
        ulong RandomState,
        bool IsGameOver);

    private sealed record EventSnapshot(
        string Type,
        string? Text,
        IReadOnlyList<Position> Cells,
        Position? From,
        Position? To,
        int? CellCount,
        int? PieceCount,
        int? EnclosureCount,
        int? Score);

    private sealed class SpyGameEngine(
        IGameEngine inner) :
        IGameEngine
    {
        public int CreateCallCount { get; private set; }

        public int PreviewCallCount { get; private set; }

        public int ExecuteCallCount { get; private set; }

        public List<string> CreatedSeeds { get; } = [];

        public List<PlacePieceCommand> ExecutedCommands { get; } = [];

        public GameState Create(string seed)
        {
            CreateCallCount++;
            CreatedSeeds.Add(seed);

            return inner.Create(seed);
        }

        public PreviewResult Preview(
            GameState state,
            PlacePieceCommand command)
        {
            PreviewCallCount++;

            return inner.Preview(state, command);
        }

        public CommandResult Execute(
            GameState state,
            PlacePieceCommand command)
        {
            ExecuteCallCount++;
            ExecutedCommands.Add(command);

            return inner.Execute(state, command);
        }
    }
}
