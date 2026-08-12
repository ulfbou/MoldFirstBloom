using Verdant.Replay;

namespace Mold.Engine;

public sealed record FirstBloomReplayInitialization(string Seed);

public sealed class FirstBloomReplayAdapter(
    IGameEngine engine) :
    IReplayAdapter<
        FirstBloomReplayInitialization,
        GameState,
        ActionEntry,
        GameEvent>
{
    public GameState CreateInitialState(
        FirstBloomReplayInitialization initialization)
    {
        ArgumentNullException.ThrowIfNull(initialization);

        return engine.Create(initialization.Seed);
    }

    public ReplayStepResult<GameState, GameEvent> Execute(
        GameState state,
        ActionEntry action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);

        var result = engine.Execute(
            state,
            new PlacePieceCommand(
                action.Slot,
                action.Origin,
                action.Rotation));

        return result.Succeeded
            ? new ReplayStepResult<GameState, GameEvent>.Accepted(
                result.State,
                result.Events)
            : new ReplayStepResult<GameState, GameEvent>.Rejected(
                ToStableFailureCode(result.Error));
    }

    internal static string ToStableFailureCode(
        CommandError error) =>
        error switch
        {
            CommandError.GameAlreadyEnded =>
                "GAME_ALREADY_ENDED",

            CommandError.InvalidHandSlot =>
                "INVALID_HAND_SLOT",

            CommandError.OutsideBoard =>
                "OUTSIDE_BOARD",

            CommandError.OccupiedCell =>
                "OCCUPIED_CELL",

            CommandError.NoConnectedCell =>
                "NO_CONNECTED_CELL",

            CommandError.None =>
                "UNEXPECTED_SUCCESS_ERROR",

            _ =>
                "UNKNOWN_COMMAND_ERROR"
        };
}
