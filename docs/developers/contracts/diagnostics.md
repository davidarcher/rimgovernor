# Diagnostic evidence and recorder limits

[Documentation](../../README.md)

The opt-in [native scenario recorder](../testing/native-scenarios.md) links native calls
and runtime snapshots and supports offline inspection and fixture export. Its bounded
coverage does not reconstruct every in-game transition. Existing evidence also includes:

| Evidence | Available behavior and limits |
| --- | --- |
| Controller SQLite (`store.py`) | Persists state, events and retired action/method evidence. Event history can include diagnostics; default history excludes diagnostic kinds. |
| `GET /api/diagnostics` | Read-only latest 100 events for the current colony, including diagnostics; not a complete run export. |
| Tool diagnostics | Planner tool arguments/results, outcomes and timing are recorded with bounded payloads. Runtime native dispatches record tool results and receipts; this is not exhaustive coverage of background reads or failed/pre-dispatch calls. |
| `ReviewEvidence` | Exact observations within a review, held in memory with a byte budget and eviction; not durable recording across process failure. |
| Decision storage helpers | `Store.decision`, `finish_decision` and `read_decision` support compressed snapshots capped at 64, but currently have no runtime callers. Do not assume a run populated them. |
| Native Docker output | Worker logs, `run/Player.log`, staging/input manifests, controller data, and any captured frames or successful paired checkpoints survive container removal. The native runner's `result.json` describes its assertions and cleanup. |

For a failed Docker run, start with `result.json` and the numbered worker's
`container.log`, then inspect native logs and retained controller evidence. A failure
before report creation may leave only console/build output. Keep the whole output tree;
a game crash may prevent a final paired checkpoint. Existing evidence cannot be assumed
to reconstruct every observation or pawn transition. The opt-in named runner adds
correlated calls, explicit truncation/gap reports, SQLite backups and offline fixture
export; use its [diagnosis procedure](../testing/native-scenarios.md#diagnose-without-the-live-game).

## Related reading

[Inspect a failed run](../testing/inspect-failure.md) · [Persistence
contracts](persistence-contracts.md)
