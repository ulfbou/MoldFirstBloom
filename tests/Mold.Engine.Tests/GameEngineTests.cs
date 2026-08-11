using Mold.Engine;
using Xunit;

namespace Mold.Engine.Tests;

public sealed class GameEngineTests
{
    private readonly GameEngine _engine = new();

    [Fact]
    public void SameSeedAndLogProduceIdenticalState()
    {
        const string seed = "A1B2C3D4";
        var log = BuildTwoActionLog(seed);

        var first = Replay.Run(_engine, seed, log);
        var second = Replay.Run(_engine, seed, log);

        AssertStatesEqual(first.State, second.State);
        Assert.Equal(first.Events.Select(EventText), second.Events.Select(EventText));
    }

    [Fact]
    public void ReplayToTurnNEqualsFreshPrefixReplay()
    {
        const string seed = "A1B2C3D4";
        var log = BuildTwoActionLog(seed);

        var partial = Replay.Run(_engine, seed, log, upTo: 1);
        var prefix = Replay.Run(_engine, seed, log.Take(1).ToArray());

        AssertStatesEqual(prefix.State, partial.State);
    }

    [Fact]
    public void InvalidCommandPreservesStateAndRandomPosition()
    {
        var state = _engine.Create("12345678");
        var invalid = new PlacePieceCommand(0, new Position(0, 0), 0);

        var result = _engine.Execute(state, invalid);

        Assert.False(result.Succeeded);
        Assert.Equal(CommandError.NoConnectedCell, result.Error);
        Assert.Same(state, result.State);
        Assert.Equal(state.RandomState, result.State.RandomState);
    }

    [Fact]
    public void AllFourRotationsAreAcceptedByPreviewContract()
    {
        var state = _engine.Create("12345678");

        for (var rotation = 0; rotation < 4; rotation++)
        {
            var preview = _engine.Preview(
                state,
                new PlacePieceCommand(1, new Position(3, 2), rotation));

            Assert.NotEqual(CommandError.InvalidHandSlot, preview.Error);
        }
    }

    private static void AssertStatesEqual(GameState expected, GameState actual)
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

    private static string EventText(GameEvent item) => item switch
    {
        PiecePlacedEvent placed => $"Place:{placed.PieceName}:{string.Join(';', placed.Cells)}",
        GrowthResolvedEvent growth => $"Grow:{growth.GrowthFrom}:{growth.GrowthTo}",
        BloomResolvedEvent bloom => $"Bloom:{bloom.CellCount}:{bloom.Score}:{string.Join(';', bloom.Cells)}",
        GameEndedEvent ended => $"End:{ended.Verdict}",
        _ => item.GetType().Name
    };

    private ActionEntry[] BuildTwoActionLog(string seed)
    {
        var initialState = _engine.Create(seed);

        var firstCommand = FindFirstValid(initialState);
        var firstResult = _engine.Execute(initialState, firstCommand);

        Assert.True(firstResult.Succeeded);

        var secondCommand = FindFirstValid(firstResult.State);
        var secondResult = _engine.Execute(firstResult.State, secondCommand);

        Assert.True(secondResult.Succeeded);

        return
        [
            ToActionEntry(firstCommand, turn: 1),
            ToActionEntry(secondCommand, turn: 2)
        ];
    }

    private static ActionEntry ToActionEntry(
        PlacePieceCommand command,
        int turn) =>
        new(
            command.Slot,
            command.Origin,
            command.Rotation,
            turn);

    private PlacePieceCommand FindFirstValid(GameState state)
    {
        for (var slot = 0; slot < state.Hand.Count; slot++)
        {
            for (var rotation = 0; rotation < 4; rotation++)
            {
                for (var y = 0; y < state.Height; y++)
                {
                    for (var x = 0; x < state.Width; x++)
                    {
                        var command = new PlacePieceCommand(slot, new Position(x, y), rotation);
                        if (_engine.Preview(state, command).IsValid)
                        {
                            return command;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("No valid command was found.");
    }
}