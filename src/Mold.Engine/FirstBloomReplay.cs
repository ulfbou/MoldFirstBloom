using Verdant.Replay;

namespace Mold.Engine;

public sealed class FirstBloomReplay
{
    private readonly ReplayEngine<
        FirstBloomReplayInitialization,
        GameState,
        ActionEntry,
        GameEvent> _engine;

    public FirstBloomReplay(IGameEngine gameEngine)
    {
        ArgumentNullException.ThrowIfNull(gameEngine);

        _engine = new ReplayEngine<
            FirstBloomReplayInitialization,
            GameState,
            ActionEntry,
            GameEvent>(
                new FirstBloomReplayAdapter(gameEngine));
    }

    public ReplayResult<GameState, GameEvent> Run(
        string seed,
        IReadOnlyList<ActionEntry> actionLog,
        int actionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        ArgumentNullException.ThrowIfNull(actionLog);

        return _engine.Replay(
            new ReplayRequest<
                FirstBloomReplayInitialization,
                ActionEntry>(
                    new FirstBloomReplayInitialization(seed),
                    actionLog,
                    actionCount));
    }

    public ReplayResult<GameState, GameEvent> Run(
        string seed,
        IReadOnlyList<ActionEntry> actionLog) =>
        Run(seed, actionLog, actionLog.Count);
}
