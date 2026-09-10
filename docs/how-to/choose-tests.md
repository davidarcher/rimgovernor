# Choose checks for a change

[Documentation](../README.md)

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Controller logic, contracts, persistence | `controller_tests/`; focused pytest or full `build.ps1` | Local Python environment; fixtures do not establish native outcomes. |
| Dashboard behavior and build | `build.ps1` runs typecheck, Vitest and Vite build | Local Python and dashboard dependencies from setup; native UI acceptance is separate. |
| Generated observation DTO matches its schema | `scripts/generate_bridge_observation.py --check` | Local Python environment; run explicitly, outside `build.ps1`. |
| Linux regression checks or independent copies of the suite | [Docker controller checks](docker-checks.md) | Host Python 3.12+ and Linux Docker; no game, mods, GABS, LM Studio, local venv or host Node required. Windows-specific tests skip. |
| Native Linux startup, isolation, clock, shutdown and checkpoint retention | [Automated native Docker acceptance](docker-native.md) | Docker Compose and staged licensed Linux game/mod/profile/GABS inputs; no model inference is exercised. |
| Rendered native container snapshots | Native Docker runner with `--display xvfb` | Same native inputs; private Xvfb/llvmpipe, no host desktop focus. Inspect retained frames. |
| Completed pawn work, recovery or gameplay invariants | Focused native probes below and [headless testing](headless-probes.md) | Disposable prepared colony, matching native DLLs and probe-specific prerequisites; read assertions and `--help`. Some probes still require Windows. |
| Actual language interpretation or sustained colony behavior | [Real model probe](semantic-commands.md#live-planner-probe), [campaigns and performance](campaigns.md) | Configured local LM Studio when inference is involved; bounded lifecycle checks do not establish these outcomes. |

For agents: inspect the affected tests and choose the smallest relevant check, then run
the full affected suite once before handoff. Controller-only changes need the full
controller suite; dashboard-only changes need typecheck, Vitest and build. Changes to
shared contracts, packaging or test infrastructure need both. Reuse a successful run
on the same revision/environment when no relevant source has changed; do not repeat
full suites after documentation-only follow-ups. Additional Docker workers repeat the
suite rather than divide it, so use one unless testing isolation or repetition.
Report commands, exit status, skips,
artifact locations and what remains unverified. Keep failed trials. A documentation-only
edit normally needs command/flag and link verification, not a new game session. Do not
mark backlog gameplay acceptance complete from fixture tests, compilation or native
receipts alone.

Use an isolated task worktree when peers may be active. A worktree does not inherit the
main checkout's `.venv` or `node_modules`; run setup there for local checks, or use the
Docker runner to build that worktree's source. Do not reuse another task's mutable image
tag or output directory.
