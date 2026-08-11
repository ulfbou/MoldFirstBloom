namespace Mold.Engine;

public enum CellMaterial
{
    Empty,
    Moss
}

public readonly record struct Position(int X, int Y)
{
    public IEnumerable<Position> OrthogonalNeighbors()
    {
        yield return new Position(X, Y - 1);
        yield return new Position(X + 1, Y);
        yield return new Position(X, Y + 1);
        yield return new Position(X - 1, Y);
    }
}

public readonly record struct PieceCell(int X, int Y);

public sealed record PieceDefinition(string Id, string Name, IReadOnlyList<PieceCell> Cells)
{
    public IReadOnlyList<PieceCell> CellsAtRotation(int rotation)
    {
        var normalized = ((rotation % 4) + 4) % 4;
        return Cells.Select(cell => Rotate(cell, normalized)).ToArray();
    }

    private static PieceCell Rotate(PieceCell cell, int rotation) => rotation switch
    {
        0 => cell,
        1 => new PieceCell(-cell.Y, cell.X),
        2 => new PieceCell(-cell.X, -cell.Y),
        3 => new PieceCell(cell.Y, -cell.X),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation))
    };
}

public static class PieceCatalog
{
    public static readonly PieceDefinition Spore = new(
        "spore",
        "Spore",
        [new PieceCell(0, 0)]);

    public static readonly PieceDefinition Pair = new(
        "pair",
        "Pair",
        [new PieceCell(0, 0), new PieceCell(1, 0)]);

    public static readonly PieceDefinition Elbow = new(
        "elbow",
        "Elbow",
        [new PieceCell(0, 0), new PieceCell(1, 0), new PieceCell(0, 1)]);

    public static PieceDefinition Get(string id) => id switch
    {
        "spore" => Spore,
        "pair" => Pair,
        "elbow" => Elbow,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown piece.")
    };
}

public sealed record HandPiece(string PieceId);

public sealed record ActionEntry(
    int Slot,
    Position Origin,
    int Rotation,
    int Turn,
    ulong? RandomPosition = null);

public sealed record GameState(
    int Width,
    int Height,
    IReadOnlyList<CellMaterial> Cells,
    IReadOnlyList<HandPiece> Hand,
    int Score,
    int Turn,
    int BloomCount,
    ulong RandomState,
    bool IsGameOver)
{
    public CellMaterial CellAt(Position position) => Cells[(position.Y * Width) + position.X];

    public bool Contains(Position position) =>
        position.X >= 0 && position.X < Width &&
        position.Y >= 0 && position.Y < Height;
}
