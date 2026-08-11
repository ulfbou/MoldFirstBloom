using System.Security.Cryptography;
using Mold.Engine;

namespace Mold.App.Services;

public enum TeachingState
{
    Idle,
    Selected,
    Preview,
    Resolved
}

public sealed record TeachingMessage(TeachingState State, string Icon, string Message);

public sealed record BoardEffect(string Kind, Position Position, string Text = "");

public sealed class GameSession(IGameEngine engine, BrowserStorage storage)
{
    private const string SaveKey = "mold.phase1.save";
    private static readonly TimeSpan CommitWindow = TimeSpan.FromMilliseconds(800);

    private Position? _previewOrigin;
    private DateTimeOffset _previewedAt;
    private int _previewRotation;
    private int? _previewSlot;

    public event Action? Changed;

    public string GameId { get; private set; } = Guid.NewGuid().ToString("N");
    public string Seed { get; private set; } = "00000001";
    public GameState State { get; private set; } = engine.Create("00000001");
    public List<ActionEntry> ActionLog { get; } = [];
    public int? SelectedSlot { get; private set; }
    public int Rotation { get; private set; }
    public PreviewResult? Preview { get; private set; }
    public bool IsResolving { get; private set; }
    public TeachingMessage Teaching { get; private set; } =
        new(TeachingState.Idle, "🌱", "Select a piece to begin.");
    public List<BoardEffect> Effects { get; } = [];

    public async Task InitializeAsync()
    {
        var save = await storage.GetAsync<SaveEnvelope>(SaveKey);
        if (save is null ||
            save.Format != SaveEnvelope.CurrentFormat ||
            save.RulesetVersion != GameEngine.RulesetVersion ||
            save.Seed.Length != 8)
        {
            await NewGameAsync();
            return;
        }

        try
        {
            var replay = Replay.Run(engine, save.Seed, save.ActionLog);
            GameId = save.GameId;
            Seed = save.Seed;
            State = replay.State;
            ActionLog.Clear();
            ActionLog.AddRange(save.ActionLog);
            Teaching = new(TeachingState.Idle, "🌱", "Run restored from deterministic replay.");
        }
        catch
        {
            await NewGameAsync();
            return;
        }

        NotifyChanged();
    }

    public void SelectPiece(int slot)
    {
        if (IsResolving || slot < 0 || slot >= State.Hand.Count)
        {
            return;
        }

        SelectedSlot = slot;
        Rotation = 0;
        CancelPreview();
        Teaching = new(TeachingState.Selected, "🧩", "Tap a valid board cell to preview.");
        NotifyChanged();
    }

    public void Rotate()
    {
        if (IsResolving || SelectedSlot is null)
        {
            return;
        }

        Rotation = (Rotation + 1) % 4;
        CancelPreview();
        Teaching = new(TeachingState.Selected, "↻", $"Rotation {Rotation + 1} of 4. Choose a board cell.");
        NotifyChanged();
    }

    public async Task TapBoardAsync(Position origin)
    {
        if (IsResolving || State.IsGameOver || SelectedSlot is null)
        {
            return;
        }

        var command = new PlacePieceCommand(SelectedSlot.Value, origin, Rotation);
        var preview = engine.Preview(State, command);
        var now = DateTimeOffset.UtcNow;

        var isConfirmation =
            preview.IsValid &&
            _previewOrigin == origin &&
            _previewSlot == SelectedSlot &&
            _previewRotation == Rotation &&
            now - _previewedAt <= CommitWindow;

        if (!preview.IsValid)
        {
            Preview = preview;
            Teaching = new(TeachingState.Preview, "⛔", ErrorMessage(preview.Error));
            NotifyChanged();
            return;
        }

        if (!isConfirmation)
        {
            Preview = preview;
            _previewOrigin = origin;
            _previewedAt = now;
            _previewRotation = Rotation;
            _previewSlot = SelectedSlot;
            Teaching = new(TeachingState.Preview, "🌱", DescribePreview(preview));
            NotifyChanged();
            return;
        }

        await CommitAsync(command);
    }

    public bool IsPreviewCell(Position position) =>
        Preview?.IsValid == true && Preview.PlacementCells.Contains(position);

    public async Task NewGameAsync()
    {
        GameId = Guid.NewGuid().ToString("N");
        Seed = RandomNumberGenerator.GetHexString(4);
        State = engine.Create(Seed);
        ActionLog.Clear();
        ResetInteraction();
        Teaching = new(TeachingState.Idle, "🌱", "Select a piece to begin.");
        await PersistAsync();
        NotifyChanged();
    }

    public async Task RematchAsync()
    {
        State = engine.Create(Seed);
        ActionLog.Clear();
        ResetInteraction();
        Teaching = new(TeachingState.Idle, "🌱", "Same seed. New history.");
        await PersistAsync();
        NotifyChanged();
    }

    private async Task CommitAsync(PlacePieceCommand command)
    {
        IsResolving = true;
        Preview = null;
        NotifyChanged();

        var result = engine.Execute(State, command);
        if (!result.Succeeded)
        {
            IsResolving = false;
            Teaching = new(TeachingState.Preview, "⛔", ErrorMessage(result.Error));
            NotifyChanged();
            return;
        }

        State = result.State;
        ActionLog.Add(new ActionEntry(
            command.Slot,
            command.Origin,
            command.Rotation,
            State.Turn));

        await PersistAsync();
        await PlayTimelineAsync(result.Events);

        ResetInteraction();
        Teaching = new(TeachingState.Resolved, "✓", DescribeResult(result.Events));
        NotifyChanged();
    }

    private async Task PlayTimelineAsync(IReadOnlyList<GameEvent> events)
    {
        Effects.Clear();
        AddEffects<PiecePlacedEvent>(events, item =>
            item.Cells.Select(position => new BoardEffect("place", position)));
        NotifyChanged();
        await Task.Delay(120);

        AddEffects<GrowthResolvedEvent>(events, item =>
            [new BoardEffect("grow", item.GrowthTo, "+")]);
        NotifyChanged();
        await Task.Delay(230);

        await Task.Delay(150); // Reserved conditional Echo window.

        AddEffects<BloomResolvedEvent>(events, item =>
            item.Cells.Select(position => new BoardEffect("bloom", position, $"+{item.Score}")));
        NotifyChanged();
        await Task.Delay(400);

        Effects.Clear();
        IsResolving = false;
    }

    private void AddEffects<TEvent>(
        IEnumerable<GameEvent> events,
        Func<TEvent, IEnumerable<BoardEffect>> factory)
        where TEvent : GameEvent
    {
        Effects.AddRange(events.OfType<TEvent>().SelectMany(factory));
    }

    private async Task PersistAsync()
    {
        var save = new SaveEnvelope(
            SaveEnvelope.CurrentFormat,
            GameId,
            GameEngine.RulesetVersion,
            Seed,
            ActionLog.ToArray(),
            State.Score);

        await storage.SetAsync(SaveKey, save);
    }

    private void CancelPreview()
    {
        Preview = null;
        _previewOrigin = null;
        _previewSlot = null;
    }

    private void ResetInteraction()
    {
        SelectedSlot = null;
        Rotation = 0;
        IsResolving = false;
        Effects.Clear();
        CancelPreview();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private static string DescribePreview(PreviewResult preview)
    {
        var growth = preview.GrowthFrom is { } from && preview.GrowthTo is { } to
            ? $"Growth: {Label(from)} → {Label(to)}. "
            : "No growth. ";
        var bloom = preview.PredictedBloomCells > 0
            ? $"Bloom {preview.PredictedBloomCells} cells +{preview.PredictedScore}. "
            : string.Empty;

        return $"{growth}{bloom}Tap the same cell within 800 ms to place.";
    }

    private static string DescribeResult(IEnumerable<GameEvent> events)
    {
        var bloom = events.OfType<BloomResolvedEvent>().SingleOrDefault();
        if (bloom is not null)
        {
            return $"Bloom! {bloom.CellCount} cells, {bloom.PieceCount} piece, +{bloom.Score}.";
        }

        var growth = events.OfType<GrowthResolvedEvent>().SingleOrDefault();
        return growth is null
            ? "Piece placed. No growth was available."
            : $"Placed, then grew {Label(growth.GrowthFrom)} → {Label(growth.GrowthTo)}.";
    }

    private static string ErrorMessage(CommandError error) => error switch
    {
        CommandError.OutsideBoard => "That shape extends outside the board.",
        CommandError.OccupiedCell => "That space is already occupied.",
        CommandError.NoConnectedCell => "The piece must touch moss orthogonally.",
        CommandError.GameAlreadyEnded => "This run has ended.",
        _ => "That placement is not valid."
    };

    private static string Label(Position position) => $"{(char)('A' + position.X)}{position.Y + 1}";
}

public sealed record SaveEnvelope(
    int Format,
    string GameId,
    string RulesetVersion,
    string Seed,
    IReadOnlyList<ActionEntry> ActionLog,
    int BestScore)
{
    public const int CurrentFormat = 3;
}
