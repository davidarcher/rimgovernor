# Development guidance

Read [AGENTS.md](AGENTS.md), [architecture](docs/ARCHITECTURE.md) and the
[single backlog](docs/BACKLOG.md). Runtime: Python controller, React dashboard,
GABS/RimBridgeServer and `integrations/colony-bridge`.

- Keep one strategist and deterministic Hands; advisers cannot write game orders.
- Preserve normal game rules. Discover installed native schemas and definitions;
  keep editor/cheat operations outside model execution.
- Target configured local LM Studio models with no paid-provider fallback.
- Verify native outcomes; receipts do not prove pawn work completed. Observe
  uncertain writes before retrying. Plans do not own arbitrary map coordinates.
- Manual, player direction and colony/load/map changes invalidate pending work.
- Preserve UI drafts and last good data during background refreshes.
- Use build.ps1 and the [testing runbook](docs/TESTING.md). Protocol tests do not
  establish autonomous gameplay. Never swap installed DLLs while RimWorld runs.
- Commit completed iterations after relevant checks; do not push unless requested.
- Keep builds, saves, local databases and logs out of commits. Maintain source
  attribution in [THIRD_PARTY.md](THIRD_PARTY.md) and integration provenance files.
