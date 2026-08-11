namespace Mold.Engine;

public sealed record PlacePieceCommand(int Slot, Position Origin, int Rotation);

public enum CommandError
{
    None,
    GameAlreadyEnded,
    InvalidHandSlot,
    OutsideBoard,
    OccupiedCell,
    NoConnectedCell
}

public abstract record GameEvent;

public sealed record PiecePlacedEvent(
    string PieceName,
    IReadOnlyList<Position> Cells) : GameEvent;

public sealed record GrowthResolvedEvent(
    Position GrowthFrom,
    Position GrowthTo) : GameEvent;

public sealed record BloomResolvedEvent(
    IReadOnlyList<Position> Cells,
    int CellCount,
    int PieceCount,
    int EnclosureCount,
    int Score) : GameEvent;

public sealed record GameEndedEvent(string Verdict) : GameEvent;

public sealed record CommandResult(
    bool Succeeded,
    GameState State,
    IReadOnlyList<GameEvent> Events,
    CommandError Error)
{
    public static CommandResult Failure(GameState state, CommandError error) =>
        new(false, state, [], error);

    public static CommandResult Success(GameState state, IReadOnlyList<GameEvent> events) =>
        new(true, state, events, CommandError.None);
}

public sealed record PreviewResult(
    bool IsValid,
    CommandError Error,
    IReadOnlyList<Position> PlacementCells,
    Position? GrowthFrom,
    Position? GrowthTo,
    int PredictedBloomCells,
    int PredictedScore);

public interface IGameEngine
{
    GameState Create(string seed);

    PreviewResult Preview(
        GameState state,
        PlacePieceCommand command);

    CommandResult Execute(
        GameState state,
        PlacePieceCommand command);
}
