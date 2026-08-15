using System.Reflection;
using Mold.Engine;
using Verdant.History;
using Verdant.Replay;
using Xunit;

namespace Mold.Engine.Tests;

public sealed class FirstBloomHistoryTests
{
    private const string Seed = "A1B2C3D4";
    private readonly GameEngine _engine = new();

    [Fact]
    public void QueryZeroReconstructsCanonicalInitialState()
    {
        var log = BuildAcceptedLog(3);
        var result = AssertHistorySuccess(
            new FirstBloomHistory(_engine).Query(Seed, log, 0));

        AssertStateEqual(_engine.Create(Seed), result.State);
        Assert.Empty(result.Events);
        Assert.Empty(result.EventBatches);
        Assert.Equal(0, result.RequestedActionCount);
        Assert.Equal(0, result.AppliedActionCount);
    }

    [Fact]
    public void EveryValidPrefixMatchesDirectVerdantReplay()
    {
        var log = BuildAcceptedLog(3);
        var history = new FirstBloomHistory(_engine);
        var replay = new FirstBloomReplay(_engine);

        for (var actionCount = 0; actionCount <= log.Count; actionCount++)
        {
            var historical = AssertHistorySuccess(
                history.Query(Seed, log, actionCount));
            var replayed = AssertReplaySuccess(
                replay.Run(Seed, log, actionCount));

            AssertStateEqual(replayed.State, historical.State);
            AssertEventsEqual(replayed.Events, historical.Events);
            AssertBatchesEqual(replayed.EventBatches, historical.EventBatches);
            Assert.Equal(actionCount, historical.RequestedActionCount);
            Assert.Equal(actionCount, historical.AppliedActionCount);
        }
    }

    [Fact]
    public void RepeatedQueriesAndNavigationArePathIndependent()
    {
        var log = BuildAcceptedLog(3);
        var history = new FirstBloomHistory(_engine);

        var fullFirst = AssertHistorySuccess(history.Query(Seed, log, 3));
        var earlier = AssertHistorySuccess(history.Query(Seed, log, 1));
        var middle = AssertHistorySuccess(history.Query(Seed, log, 2));
        var fullAgain = AssertHistorySuccess(history.Query(Seed, log, 3));

        AssertHistoryEqual(DirectQuery(log, 3), fullFirst);
        AssertHistoryEqual(DirectQuery(log, 1), earlier);
        AssertHistoryEqual(DirectQuery(log, 2), middle);
        AssertHistoryEqual(fullFirst, fullAgain);
        Assert.NotSame(fullFirst.State, fullAgain.State);
        Assert.NotSame(fullFirst.Events, fullAgain.Events);
        Assert.NotSame(fullFirst.EventBatches, fullAgain.EventBatches);
    }

    [Fact]
    public void LaterQueriesDoNotMutateEarlierResultsOrLiveInputs()
    {
        var liveState = _engine.Create(Seed);
        var liveStateBefore = CaptureState(liveState);
        var liveLog = BuildAcceptedLog(3);
        var liveLogBefore = liveLog.ToArray();
        var history = new FirstBloomHistory(_engine);
        var earlier = AssertHistorySuccess(history.Query(Seed, liveLog, 1));
        var earlierState = CaptureState(earlier.State);
        var earlierEvents = earlier.Events.Select(CaptureEvent).ToArray();
        var earlierBatches = earlier.EventBatches
            .Select(batch => batch.Select(CaptureEvent).ToArray())
            .ToArray();

        _ = AssertHistorySuccess(history.Query(Seed, liveLog, 3));
        _ = AssertHistorySuccess(history.Query(Seed, liveLog, 2));

        Assert.Equal(liveStateBefore, CaptureState(liveState));
        Assert.Equal(liveLogBefore, liveLog);
        Assert.Equal(earlierState, CaptureState(earlier.State));
        Assert.Equal(earlierEvents, earlier.Events.Select(CaptureEvent));
        Assert.Equal(earlierBatches.Length, earlier.EventBatches.Count);
        for (var index = 0; index < earlierBatches.Length; index++)
        {
            Assert.Equal(
                earlierBatches[index],
                earlier.EventBatches[index].Select(CaptureEvent));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(999)]
    public void InvalidPrefixPreservesVerdantReplayFailure(int actionCount)
    {
        var log = BuildAcceptedLog(3);
        var failure = AssertHistoryFailure(
            new FirstBloomHistory(_engine).Query(Seed, log, actionCount));

        Assert.Equal(ReplayErrorCode.InvalidReplayActionCount, failure.Error.Code);
        Assert.Null(failure.Error.ActionIndex);
        Assert.Null(failure.Error.AdapterFailureCode);
    }

    [Fact]
    public void HistoricalServiceDelegatesToVerdantHistoryAndSharedReplayAdapter()
    {
        var fields = typeof(FirstBloomHistory).GetFields(
            BindingFlags.NonPublic | BindingFlags.Instance);
        var field = Assert.Single(fields);

        Assert.Equal(
            typeof(HistoricalQueryService<,,,>),
            field.FieldType.GetGenericTypeDefinition());
        Assert.Single(typeof(FirstBloomReplayAdapter).GetConstructors());
    }

    private HistoricalQueryResult<GameState, GameEvent>.Success DirectQuery(
        IReadOnlyList<ActionEntry> log,
        int actionCount) =>
        AssertHistorySuccess(
            new FirstBloomHistory(_engine).Query(Seed, log, actionCount));

    private List<ActionEntry> BuildAcceptedLog(int actionCount)
    {
        var state = _engine.Create(Seed);
        var log = new List<ActionEntry>();

        for (var index = 0; index < actionCount; index++)
        {
            var command = FindFirstValid(state);
            var result = _engine.Execute(state, command);
            Assert.True(result.Succeeded);
            log.Add(new ActionEntry(
                command.Slot,
                command.Origin,
                command.Rotation,
                result.State.Turn));
            state = result.State;
        }

        return log;
    }

    private PlacePieceCommand FindFirstValid(GameState state)
    {
        for (var slot = 0; slot < state.Hand.Count; slot++)
        for (var rotation = 0; rotation < 4; rotation++)
        for (var y = 0; y < state.Height; y++)
        for (var x = 0; x < state.Width; x++)
        {
            var command = new PlacePieceCommand(
                slot,
                new Position(x, y),
                rotation);
            if (_engine.Preview(state, command).IsValid)
            {
                return command;
            }
        }

        throw new InvalidOperationException("No valid command was found.");
    }

    private static HistoricalQueryResult<GameState, GameEvent>.Success
        AssertHistorySuccess(HistoricalQueryResult<GameState, GameEvent> result) =>
        Assert.IsType<HistoricalQueryResult<GameState, GameEvent>.Success>(result);

    private static HistoricalQueryResult<GameState, GameEvent>.Failure
        AssertHistoryFailure(HistoricalQueryResult<GameState, GameEvent> result) =>
        Assert.IsType<HistoricalQueryResult<GameState, GameEvent>.Failure>(result);

    private static ReplayResult<GameState, GameEvent>.Success
        AssertReplaySuccess(ReplayResult<GameState, GameEvent> result) =>
        Assert.IsType<ReplayResult<GameState, GameEvent>.Success>(result);

    private static void AssertHistoryEqual(
        HistoricalQueryResult<GameState, GameEvent>.Success expected,
        HistoricalQueryResult<GameState, GameEvent>.Success actual)
    {
        AssertStateEqual(expected.State, actual.State);
        AssertEventsEqual(expected.Events, actual.Events);
        AssertBatchesEqual(expected.EventBatches, actual.EventBatches);
        Assert.Equal(expected.RequestedActionCount, actual.RequestedActionCount);
        Assert.Equal(expected.AppliedActionCount, actual.AppliedActionCount);
    }

    private static void AssertStateEqual(GameState expected, GameState actual)
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

    private static void AssertEventsEqual(
        IReadOnlyList<GameEvent> expected,
        IReadOnlyList<GameEvent> actual) =>
        Assert.Equal(expected.Select(CaptureEvent), actual.Select(CaptureEvent));

    private static void AssertBatchesEqual(
        IReadOnlyList<IReadOnlyList<GameEvent>> expected,
        IReadOnlyList<IReadOnlyList<GameEvent>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertEventsEqual(expected[index], actual[index]);
        }
    }

    private static StateSnapshot CaptureState(GameState state) =>
        new(
            state.Width,
            state.Height,
            string.Join(',', state.Cells),
            string.Join(',', state.Hand.Select(item => item.PieceId)),
            state.Score,
            state.Turn,
            state.BloomCount,
            state.RandomState,
            state.IsGameOver);

    private static string CaptureEvent(GameEvent item) => item switch
    {
        PiecePlacedEvent placed =>
            $"Place:{placed.PieceName}:{string.Join(';', placed.Cells)}",
        GrowthResolvedEvent growth =>
            $"Grow:{growth.GrowthFrom}:{growth.GrowthTo}",
        BloomResolvedEvent bloom =>
            $"Bloom:{string.Join(';', bloom.Cells)}:{bloom.CellCount}:" +
            $"{bloom.PieceCount}:{bloom.EnclosureCount}:{bloom.Score}",
        GameEndedEvent ended => $"End:{ended.Verdict}",
        _ => throw new InvalidOperationException(
            $"No structural event representation exists for {item.GetType().FullName}.")
    };

    private sealed record StateSnapshot(
        int Width,
        int Height,
        string Cells,
        string Hand,
        int Score,
        int Turn,
        int BloomCount,
        ulong RandomState,
        bool IsGameOver);
}