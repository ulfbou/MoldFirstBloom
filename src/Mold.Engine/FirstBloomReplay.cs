using Verdant.Replay;

namespace Mold.Engine;

public sealed class FirstBloomReplay
{
    private readonly ReplayEngine<
        FirstBloomReplayInitialization,
        GameState,
        ActionEntry,
        GameEvent> _replay;

    public FirstBloomReplay(IGameEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _replay = new ReplayEngine<
            FirstBloomReplayInitialization,
            GameState,
            ActionEntry,
            GameEvent>(
                new FirstBloomReplayAdapter(engine));
    }

    public ReplayResult<GameState, GameEvent> Run(
        string seed,
        IReadOnlyList<ActionEntry> actionLog,
        int actionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        ArgumentNullException.ThrowIfNull(actionLog);

        return _replay.Replay(
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
