namespace Mold.Engine;

public sealed record ReplayResult(
    GameState State,
    IReadOnlyList<GameEvent> Events,
    int AppliedActions);

public static class Replay
{
    public static ReplayResult Run(
        IGameEngine engine,
        string seed,
        IReadOnlyList<ActionEntry> log,
        int? upTo = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(log);

        var limit = upTo is null
            ? log.Count
            : Math.Clamp(upTo.Value, 0, log.Count);

        var state = engine.Create(seed);
        var events = new List<GameEvent>();

        for (var index = 0; index < limit; index++)
        {
            var entry = log[index];
            var result = engine.Execute(
                state,
                new PlacePieceCommand(entry.Slot, entry.Origin, entry.Rotation));

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Replay rejected action {index} with {result.Error}.");
            }

            state = result.State;
            events.AddRange(result.Events);
        }

        return new ReplayResult(state, events, limit);
    }
}
