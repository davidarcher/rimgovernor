# Choose checks for a change

[Documentation](../../README.md)

## Testing budget and evidence reuse

Follow the testing pyramid: many fast unit tests, fewer boundary integration tests
and a small set of targeted game acceptance scenarios. Keep focused unit tests in
the edit loop. Before a slow check, identify the changed behavior or unresolved
failure it verifies and why cheaper checks cannot establish it.

Use fixtures, replay and contract tests for most Python-to-Go migration parity.
Parameterize biome and colony policy variants where simulation is unnecessary.
Run native acceptance at relevant feature milestones. Broad scenario matrices and
sustained campaigns are separate scheduled or explicitly requested work. After an
acceptance failure, add a fast regression test where feasible and rerun the affected
scenario; expand only when changed behavior or the failure justifies it.

Passing evidence follows relevant code, dependencies, inputs and environment, not
the main HEAD hash. Unrelated main commits, clean cherry-picks and rebases do not
invalidate it. Inspect the relevant diff and reuse applicable results across agents.
Conflict resolution or dependency changes require only the checks they affect.
The full affected suite below means the applicable automated suite, not every
gameplay scenario. Finish when agreed completion criteria and relevant checks pass;
put unrelated discoveries in the backlog.

## Available checks

Native inputs on this host: [shared Windows/Linux store and worktree commands](local-acceptance-inputs.md).

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Gated Go module/replay tools | From `go/`: `go test ./...`, `go vet ./...`, `go mod verify`, `go mod tidy -diff`; Go CI also checks formatting and Windows/Linux builds. | Pin Go via `go/.go-version`; Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks. See [Go checks](../../../go/README.md). |
| Shared Protobuf contracts | Official C#/Go generation `--check`, both Go wire/proof modules, net472 binary/ProtoJSON exchange and reciprocal fixture verification. | [Generation commands](../../../contracts/schema-generation.md); Protobuf CI covers Windows and Linux/Mono. Native adapters additionally need gameplay acceptance. |
| Controller logic, contracts, persistence | `controller_tests/`; focused pytest or full `build.ps1` | Local Python environment; fixtures do not establish native outcomes. |
| Dashboard behavior and build | `build.ps1` runs typecheck, Vitest and Vite build | Local Python and dashboard dependencies from setup; native UI acceptance is separate. |
| Generated observation DTO matches its schema | `scripts/generate_bridge_observation.py --check` | Local Python environment; run explicitly, outside `build.ps1`. |
| Linux regression checks or independent copies of the suite | [Docker controller checks](docker-checks.md) | Host Python 3.12+ and Linux Docker; no game, mods, GABS, LM Studio, local venv or host Node required. Windows-specific tests skip. |
| Native Linux startup, isolation, clock, shutdown and checkpoint retention | [Automated native Docker acceptance](docker-native.md) | Docker Compose and staged licensed Linux game/mod/profile/GABS inputs; no model inference is exercised. |
| Scenario dashboard isolation and discovery | [Standard scenario launcher](scenario-launcher.md), `scenario_dashboard_acceptance.py` | Paused native tick/direction invariance under retained HTTP reads; fixture tests cover refused mutations and runtime replacement. |
| Rendered native container snapshots | Native Docker runner with `--display xvfb` | Same native inputs; private Xvfb/llvmpipe, no host desktop focus. Inspect retained frames. |
| Completed pawn work, recovery or gameplay invariants | Focused native probes below and [headless testing](headless-probes.md) | Disposable prepared colony, matching native DLLs and probe-specific prerequisites; read assertions and `--help`. Some probes still require Windows. |
| Surface/deep extraction, storage and mining save identity | [Material extraction acceptance](mining-acceptance.md), `test_production_policy.py`, `test_extraction_development.py` | Private Linux worker and test-only mining fixtures; verifies actual pawn output separately from controller fixtures. |
| Actual language interpretation or sustained colony behavior | [Real model probe](semantic-commands.md#live-planner-probe), [campaigns and performance](campaigns.md) | Configured local LM Studio when inference is involved; bounded lifecycle checks do not establish these outcomes. |

For agents: inspect the affected tests and choose the smallest relevant check, then run
the full affected suite once before handoff. Controller-only changes need the full
controller suite; dashboard-only changes need typecheck, Vitest and build. Changes to
shared contracts, packaging or test infrastructure need both. Reuse a successful run
when relevant code, dependencies, inputs and environment are unchanged, even if main
has advanced; do not repeat
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
