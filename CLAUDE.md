# Development guidance

This fork is a single colony manager, not the upstream per-pawn simulator.

- Never commit or push unless the user explicitly asks.
- Preserve ordinary game rules. No difficulty/speed/ability patches, pawn spawning/deletion, or ownership restrictions.
- LM Studio is the default. Never silently fall back to a paid provider.
- Keep prompts, tool responses and query areas compact. The user explicitly wants uncapped local review turns/actions and no mod-imposed local output-token cap; paid-provider budgets remain separate.
- Game access stays on the main thread. Discard proposed actions after pause or game/map/provider changes.
- Use build.ps1 for regression/package and build.ps1 -Live for a synthetic local model test.
- Temporary artifacts belong in tmp/ (ignored). Never replace a DLL while the game is running.
- Game smoke tests use isolated saves and compile-only test code. Review their logs/screenshots and record results in TODO.md. Never ship the harness.
- Do not silently repair legacy saves. They may contain upstream changes that cannot be reversed.

## Architectural direction

The current build is an interim checkpoint. Follow docs/PLAYER_API_DIRECTION.md: expose existing RimWorld observations and player controls through a thin, discoverable adapter. Do not keep expanding hard-coded shelter strategies or recreate social/mood/job/room simulation. AI strategy should emerge from game observations and player direction.
