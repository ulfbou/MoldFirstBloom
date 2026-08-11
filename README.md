# MOLD: First Bloom - Phase 1 Sample

A small, dependency-light Blazor WebAssembly `.NET 10` vertical slice.

## Included

- Locked mobile `100dvh` shell and always-mounted board
- Touch-first two-tap placement: preview, then commit within 800 ms
- Four rotations
- Pure deterministic engine boundary
- Seeded, versioned RNG state
- Place -> Grow -> optional Bloom semantic events
- 900 ms presentation lock and timeline
- Teaching Strip states
- Action Log and replay
- Local-storage save envelope and reload resume
- Overgrown game-over detection
- Unit tests for replay, invalid commands, and determinism

## Intentionally deferred

Fertile, Barren, Echo, Museum, Timeline UI, sharing, board export, audio, haptics,
organism SVG lines, and advanced rules. Extension points are present where useful,
but no deferred mechanic is exposed as a broken feature.

## Run

```bash
dotnet restore Mold.slnx
dotnet test Mold.slnx
dotnet run --project src/Mold.App
```

Open the HTTPS URL shown by the CLI. Use browser device emulation for mobile checks.

## Architecture

`Mold.Engine` contains no browser, DOM, animation, storage, or Blazor references.
The app sends validated commands to the engine, persists successful actions, and
renders ordered semantic events. Replay rebuilds state from seed, ruleset version,
and the action log.

This sample intentionally uses plain IndexedDB through a small, dependency-free JS adapter to keep
Phase 1 reviewable. A later phase can replace the adapter without changing the
save or engine contracts.


## Phase 1 completion notes

The shell includes non-functional framework sheets for deferred systems. They are
intentionally labeled as later-phase placeholders and cannot mutate the active run.
The active save envelope is stored in IndexedDB (`mold-db`, schema version 3), while
the Action Log remains authoritative and reload always reconstructs through replay.

## Placement contract

A piece may be placed at any origin where all transformed cells are in bounds
and empty. The base rules do not require contact with existing moss. Placement
remains preview-first and commits only on a second tap of the same origin within
800 ms.