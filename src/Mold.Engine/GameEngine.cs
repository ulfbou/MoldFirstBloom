namespace Mold.Engine;

public sealed class GameEngine : IGameEngine
{
    public const string RulesetVersion = "first-bloom-phase1-1.0";
    public const int BoardSize = 7;
    public const int BloomThreshold = 5;

    public GameState Create(string seed)
    {
        var cells = Enumerable.Repeat(CellMaterial.Empty, BoardSize * BoardSize).ToArray();
        var center = new Position(BoardSize / 2, BoardSize / 2);
        cells[IndexOf(center)] = CellMaterial.Moss;

        return new GameState(
            BoardSize,
            BoardSize,
            cells,
            [new HandPiece("spore"), new HandPiece("pair"), new HandPiece("elbow")],
            Score: 0,
            Turn: 0,
            BloomCount: 0,
            RandomState: SeededRandom.FromHexSeed(seed),
            IsGameOver: false);
    }

    public PreviewResult Preview(GameState state, PlacePieceCommand command)
    {
        var validation = Validate(state, command);
        if (validation.Error != CommandError.None)
        {
            return new PreviewResult(false, validation.Error, validation.Cells, null, null, 0, 0);
        }

        var simulatedCells = state.Cells.ToArray();
        foreach (var position in validation.Cells)
        {
            simulatedCells[IndexOf(position)] = CellMaterial.Moss;
        }

        var (growthFrom, growthTo, _) = FindGrowth(state with { Cells = simulatedCells });
        if (growthTo is { } destination)
        {
            simulatedCells[IndexOf(destination)] = CellMaterial.Moss;
        }

        var component = FindLargestComponent(state with { Cells = simulatedCells });
        var predictedBloom = component.Count >= BloomThreshold ? component.Count : 0;

        return new PreviewResult(
            true,
            CommandError.None,
            validation.Cells,
            growthFrom,
            growthTo,
            predictedBloom,
            predictedBloom * 10);
    }

    public CommandResult Execute(GameState state, PlacePieceCommand command)
    {
        var validation = Validate(state, command);
        if (validation.Error != CommandError.None)
        {
            return CommandResult.Failure(state, validation.Error);
        }

        var cells = state.Cells.ToArray();
        foreach (var position in validation.Cells)
        {
            cells[IndexOf(position)] = CellMaterial.Moss;
        }

        var hand = state.Hand.ToList();
        var piece = PieceCatalog.Get(hand[command.Slot].PieceId);
        hand.RemoveAt(command.Slot);

        var events = new List<GameEvent>
        {
            new PiecePlacedEvent(piece.Name, validation.Cells)
        };

        var working = state with { Cells = cells };
        var (growthFrom, growthTo, randomState) = FindGrowth(working);

        if (growthFrom is { } source && growthTo is { } destination)
        {
            cells[IndexOf(destination)] = CellMaterial.Moss;
            events.Add(new GrowthResolvedEvent(source, destination));
        }

        var component = FindLargestComponent(state with { Cells = cells });
        var score = state.Score;
        var bloomCount = state.BloomCount;

        if (component.Count >= BloomThreshold)
        {
            var bloomScore = component.Count * 10;
            score += bloomScore;
            bloomCount++;

            foreach (var position in component)
            {
                cells[IndexOf(position)] = CellMaterial.Empty;
            }

            events.Add(new BloomResolvedEvent(
                component,
                component.Count,
                PieceCount: 1,
                EnclosureCount: 0,
                bloomScore));
        }

        if (hand.Count == 0)
        {
            hand.AddRange([
                new HandPiece("spore"),
                new HandPiece("pair"),
                new HandPiece("elbow")
            ]);
        }

        var next = state with
        {
            Cells = cells,
            Hand = hand,
            Score = score,
            Turn = state.Turn + 1,
            BloomCount = bloomCount,
            RandomState = randomState
        };

        var isGameOver = !HasAnyValidPlacement(next);
        if (isGameOver)
        {
            next = next with { IsGameOver = true };
            events.Add(new GameEndedEvent("Garden Overgrown"));
        }

        return CommandResult.Success(next, events);
    }

    private static (CommandError Error, IReadOnlyList<Position> Cells) Validate(
        GameState state,
        PlacePieceCommand command)
    {
        if (state.IsGameOver)
        {
            return (CommandError.GameAlreadyEnded, []);
        }

        if (command.Slot < 0 || command.Slot >= state.Hand.Count)
        {
            return (CommandError.InvalidHandSlot, []);
        }

        var piece = PieceCatalog.Get(state.Hand[command.Slot].PieceId);
        var cells = piece.CellsAtRotation(command.Rotation)
            .Select(cell => new Position(command.Origin.X + cell.X, command.Origin.Y + cell.Y))
            .ToArray();

        if (cells.Any(position => !state.Contains(position)))
        {
            return (CommandError.OutsideBoard, cells);
        }

        if (cells.Any(position => state.CellAt(position) != CellMaterial.Empty))
        {
            return (CommandError.OccupiedCell, cells);
        }

        return (CommandError.None, cells);
    }

    private static (Position? From, Position? To, ulong RandomState) FindGrowth(GameState state)
    {
        var candidates = new List<(Position From, Position To)>();

        for (var y = 0; y < state.Height; y++)
        {
            for (var x = 0; x < state.Width; x++)
            {
                var source = new Position(x, y);
                if (state.CellAt(source) != CellMaterial.Moss)
                {
                    continue;
                }

                foreach (var destination in source.OrthogonalNeighbors())
                {
                    if (state.Contains(destination) && state.CellAt(destination) == CellMaterial.Empty)
                    {
                        candidates.Add((source, destination));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            return (null, null, state.RandomState);
        }

        var (randomState, index) = SeededRandom.Next(state.RandomState, candidates.Count);
        var selected = candidates[index];
        return (selected.From, selected.To, randomState);
    }

    private static List<Position> FindLargestComponent(GameState state)
    {
        var visited = new HashSet<Position>();
        var largest = new List<Position>();

        for (var y = 0; y < state.Height; y++)
        {
            for (var x = 0; x < state.Width; x++)
            {
                var start = new Position(x, y);
                if (state.CellAt(start) != CellMaterial.Moss || !visited.Add(start))
                {
                    continue;
                }

                var component = new List<Position>();
                var queue = new Queue<Position>();
                queue.Enqueue(start);

                while (queue.TryDequeue(out var current))
                {
                    component.Add(current);

                    foreach (var neighbor in current.OrthogonalNeighbors())
                    {
                        if (state.Contains(neighbor) &&
                            state.CellAt(neighbor) == CellMaterial.Moss &&
                            visited.Add(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                if (component.Count > largest.Count)
                {
                    largest = component;
                }
            }
        }

        return largest;
    }

    private static bool HasAnyValidPlacement(GameState state)
    {
        for (var slot = 0; slot < state.Hand.Count; slot++)
        {
            for (var rotation = 0; rotation < 4; rotation++)
            {
                for (var y = 0; y < state.Height; y++)
                {
                    for (var x = 0; x < state.Width; x++)
                    {
                        if (Validate(state, new PlacePieceCommand(slot, new Position(x, y), rotation)).Error == CommandError.None)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static int IndexOf(Position position) => (position.Y * BoardSize) + position.X;
}