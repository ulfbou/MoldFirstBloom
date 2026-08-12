using Mold.Engine;
using Verdant.Replay;
using Xunit;

namespace Mold.Engine.Tests;

public sealed class VerdantReplayMigrationTests
{
    private const string Seed = "A1B2C3D4";

    private readonly GameEngine _engine = new();

    [Fact]
    public void EveryValidPrefixMatchesLegacyReplayStateAndEvents()
    {
        var log = BuildTwoActionLog();
        var verdantReplay = new FirstBloomReplay(_engine);

        for (var actionCount = 0;
             actionCount <= log.Count;
             actionCount++)
        {
            var legacy = Replay.Run(
                _engine,
                Seed,
                log,
                actionCount);

            var migrated = AssertSuccess(
                verdantReplay.Run(
                    Seed,
                    log,
                    actionCount));

            AssertStatesEqual(legacy.State, migrated.State);

            Assert.Equal(
                legacy.Events.Select(EventText),
                migrated.Events.Select(EventText));

            Assert.Equal(
                actionCount,
                migrated.AppliedActionCount);
        }
    }

    [Fact]
    public void EveryPrefixIsReconstructedIndependently()
    {
        var log = BuildTwoActionLog();
        var replay = new FirstBloomReplay(_engine);

        var zero = AssertSuccess(
            replay.Run(Seed, log, 0));

        var one = AssertSuccess(
            replay.Run(Seed, log, 1));

        var two = AssertSuccess(
            replay.Run(Seed, log, 2));

        var zeroAgain = AssertSuccess(
            replay.Run(Seed, log, 0));

        Assert.Equal(0, zero.AppliedActionCount);
        Assert.Equal(1, one.AppliedActionCount);
        Assert.Equal(2, two.AppliedActionCount);

        Assert.Equal(0, zero.State.Turn);
        Assert.Equal(1, one.State.Turn);
        Assert.Equal(2, two.State.Turn);

        AssertStatesEqual(zero.State, zeroAgain.State);
        Assert.Equal(
            zero.Events.Select(EventText),
            zeroAgain.Events.Select(EventText));
    }

    [Fact]
    public void ReplayZeroEqualsCanonicalInitialState()
    {
        var log = BuildTwoActionLog();
        var replay = new FirstBloomReplay(_engine);
        var initial = _engine.Create(Seed);

        var result = AssertSuccess(
            replay.Run(Seed, log, 0));

        AssertStatesEqual(initial, result.State);
        Assert.Empty(result.Events);
        Assert.Empty(result.EventBatches);
        Assert.Equal(0, result.AppliedActionCount);
    }

    [Fact]
    public void ReplayPreservesPerActionEventBatches()
    {
        var log = BuildTwoActionLog();
        var replay = new FirstBloomReplay(_engine);

        var result = AssertSuccess(
            replay.Run(Seed, log, log.Count));

        Assert.Equal(log.Count, result.EventBatches.Count);

        var flattened = result.EventBatches
            .SelectMany(batch => batch)
            .Select(EventText);

        Assert.Equal(
            result.Events.Select(EventText),
            flattened);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(999)]
    public void InvalidPrefixReturnsTypedFailureWithoutClamping(
        int actionCount)
    {
        var log = BuildTwoActionLog();
        var replay = new FirstBloomReplay(_engine);

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
    }

    [Fact]
    public void RejectedAuthoritativeActionReturnsStableFailure()
    {
        var initial = _engine.Create(Seed);

        IReadOnlyList<ActionEntry> log =
        [
            new ActionEntry(
                0,
                new Position(
                    initial.Width / 2,
                    initial.Height / 2),
                0,
                1)
        ];

        var replay = new FirstBloomReplay(_engine);

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

        Assert.Equal(0, failure.Error.ActionIndex);
        Assert.Equal(
            "OCCUPIED_CELL",
            failure.Error.AdapterFailureCode);
    }

    [Fact]
    public void RepeatedReplayProducesEquivalentStateAndEvents()
    {
        var log = BuildTwoActionLog();
        var replay = new FirstBloomReplay(_engine);

        var first = AssertSuccess(
            replay.Run(Seed, log));

        var second = AssertSuccess(
            replay.Run(Seed, log));

        var third = AssertSuccess(
            replay.Run(Seed, log));

        AssertStatesEqual(first.State, second.State);
        AssertStatesEqual(second.State, third.State);

        Assert.Equal(
            first.Events.Select(EventText),
            second.Events.Select(EventText));

        Assert.Equal(
            second.Events.Select(EventText),
            third.Events.Select(EventText));
    }

    [Fact]
    public void ReplayDoesNotModifySuppliedActionLog()
    {
        var log = BuildTwoActionLog().ToList();
        var original = log.ToArray();
        var replay = new FirstBloomReplay(_engine);

        _ = AssertSuccess(
            replay.Run(
                Seed,
                log,
                log.Count));

        Assert.Equal(original, log);
    }

    [Fact]
    public void ReplayDoesNotModifyExistingLiveState()
    {
        var log = BuildTwoActionLog();
        var liveState = _engine.Create(Seed);
        var before = Snapshot(liveState);
        var replay = new FirstBloomReplay(_engine);

        _ = AssertSuccess(
            replay.Run(
                Seed,
                log,
                log.Count));

        Assert.Equal(before, Snapshot(liveState));
    }

    [Fact]
    public void AdapterReusesExistingDeterministicEngineExecution()
    {
        var state = _engine.Create(Seed);
        var command = FindFirstValid(state);
        var direct = _engine.Execute(state, command);

        Assert.True(direct.Succeeded);

        var entry = ToActionEntry(
            command,
            direct.State.Turn);

        var adapter =
            new FirstBloomReplayAdapter(_engine);

        var adapted = adapter.Execute(
            state,
            entry);

        var accepted =
            Assert.IsType<
                ReplayStepResult<
                    GameState,
                    GameEvent>.Accepted>(
                        adapted);

        AssertStatesEqual(
            direct.State,
            accepted.State);

        Assert.Equal(
            direct.Events.Select(EventText),
            accepted.Events.Select(EventText));
    }

    private IReadOnlyList<ActionEntry> BuildTwoActionLog()
    {
        var initialState = _engine.Create(Seed);

        var firstCommand =
            FindFirstValid(initialState);

        var firstResult =
            _engine.Execute(
                initialState,
                firstCommand);

        Assert.True(firstResult.Succeeded);

        var secondCommand =
            FindFirstValid(firstResult.State);

        var secondResult =
            _engine.Execute(
                firstResult.State,
                secondCommand);

        Assert.True(secondResult.Succeeded);

        return
        [
            ToActionEntry(
                firstCommand,
                firstResult.State.Turn),

            ToActionEntry(
                secondCommand,
                secondResult.State.Turn)
        ];
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

    private static void AssertStatesEqual(
        GameState expected,
        GameState actual)
    {
        Assert.Equal(
            expected.Width,
            actual.Width);

        Assert.Equal(
            expected.Height,
            actual.Height);

        Assert.Equal(
            expected.Cells,
            actual.Cells);

        Assert.Equal(
            expected.Hand,
            actual.Hand);

        Assert.Equal(
            expected.Score,
            actual.Score);

        Assert.Equal(
            expected.Turn,
            actual.Turn);

        Assert.Equal(
            expected.BloomCount,
            actual.BloomCount);

        Assert.Equal(
            expected.RandomState,
            actual.RandomState);

        Assert.Equal(
            expected.IsGameOver,
            actual.IsGameOver);
    }

    private static string Snapshot(
        GameState state) =>
        string.Join(
            "|",
            state.Width,
            state.Height,
            string.Join(",", state.Cells),
            string.Join(",", state.Hand),
            state.Score,
            state.Turn,
            state.BloomCount,
            state.RandomState,
            state.IsGameOver);

    private static string EventText(
        GameEvent item) =>
        item switch
        {
            PiecePlacedEvent placed =>
                $"Place:{placed.PieceName}:" +
                string.Join(';', placed.Cells),

            GrowthResolvedEvent growth =>
                $"Grow:{growth.GrowthFrom}:" +
                $"{growth.GrowthTo}",

            BloomResolvedEvent bloom =>
                $"Bloom:{bloom.CellCount}:" +
                $"{bloom.Score}:" +
                string.Join(';', bloom.Cells),

            GameEndedEvent ended =>
                $"End:{ended.Verdict}",

            _ =>
                item.GetType().Name
        };
}
