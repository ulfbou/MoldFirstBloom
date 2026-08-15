using Verdant.History;

namespace Mold.Engine;

public sealed class FirstBloomHistory
{
    private readonly HistoricalQueryService<
        FirstBloomReplayInitialization,
        GameState,
        ActionEntry,
        GameEvent> _history;

    public FirstBloomHistory(IGameEngine gameEngine)
    {
        ArgumentNullException.ThrowIfNull(gameEngine);

        _history = new HistoricalQueryService<
            FirstBloomReplayInitialization,
            GameState,
            ActionEntry,
            GameEvent>(new FirstBloomReplayAdapter(gameEngine));
    }

    public HistoricalQueryResult<GameState, GameEvent> Query(
        string seed,
        IReadOnlyList<ActionEntry> actionLog,
        int actionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        ArgumentNullException.ThrowIfNull(actionLog);

        return _history.Query(
            new FirstBloomReplayInitialization(seed),
            actionLog,
            actionCount);
    }

    public HistoricalQueryResult<GameState, GameEvent> Query(
        string seed,
        IReadOnlyList<ActionEntry> actionLog) =>
        Query(seed, actionLog, actionLog.Count);
}