# Choose checks for a change

[Documentation](../../README.md) · [Development workflow](../development-process.md)

## Testing budget and evidence reuse

Follow the testing pyramid: many fast Go unit tests, fewer boundary integration
tests and a small set of targeted native acceptance harnesses verified against a
real headless RimWorld instance. Keep focused unit tests in the edit loop.
Before a slow check, identify the changed behavior or unresolved failure it
verifies and why cheaper checks cannot establish it.

Run native acceptance at relevant feature milestones, not after every edit.
After an acceptance failure, add a fast regression test where feasible and
rerun the affected harness; expand coverage only when the changed behavior or
failure justifies it. Do not duplicate tests or reviews already supported by
applicable evidence.

Passing evidence follows relevant code, dependencies, inputs and environment,
not the main HEAD hash. Unrelated main commits, clean cherry-picks and rebases
do not invalidate it. Inspect the relevant diff and reuse applicable results
across agents; conflict resolution or dependency changes require only the
checks they affect. "The full affected suite" means the applicable automated
suite, not every gameplay scenario. Finish when agreed completion criteria and
relevant checks pass; put unrelated discoveries in the backlog.

## Available checks

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Go controller logic, contracts, persistence | From `go/`: `go test ./...`, `go vet ./...`, `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (also run together by [build.ps1](../../../build.ps1)) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks below. See [go/README.md](../../../go/README.md). |
| Dashboard behavior and build | `build.ps1` runs `pnpm run typecheck`, `pnpm run test` (Vitest) and `pnpm run build` from `dashboard/` | Local pnpm and dashboard dependencies; native UI acceptance is separate. |
| Shared Protobuf contracts | Official C#/Go generation `--check` for both languages | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. |
| Completed pawn work, recovery or another live-game invariant | A targeted `go/internal/nativeaccept/cmd/*accept` harness (`bedassignaccept`, `caravancontrolaccept`, `caravandepartureaccept`, `combataccept`, `constructionaccept`, `draftaccept`, `husbandryaccept`, `movementaccept`, `questacceptaccept`, `questfulfillaccept`, `recoveryareaaccept`, `recoveryserviceaccept`, `researchaccept`, `roomsaccept`, `settlementgiftaccept`, `sustainedfoodaccept`, `tradeaccept`, `zoneaccept` and others — see the current set under `go/internal/nativeaccept/cmd/`) | Disposable prepared colony, matching native DLLs, GABS and a real headless RimWorld instance. Never replace installed DLLs while any RimWorld instance is running, including another worktree's tests. Isolated tests must restore temporarily swapped DLLs. A receipt alone does not prove pawn work completed — verify the observed postcondition. |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` environment variable (colony, food forecast, production, naming, power methods, temperature methods, mood — see [go/README.md](../../../go/README.md) for the exact test name and variable per family) | Retained native capture files; verifies decoding/replay parity, not a live game session. |
| Docker packaging of the Go controller | `docker build -f containers/Dockerfile --target go-controller`, `docker run --rm rimgovernor-go:local version`/`help` | Confirms the image builds and the binary starts without a licensed game; not gameplay evidence. See [go/README.md](../../../go/README.md#go-launch-and-packaging-the-production-default-g0111-g0112). |

For agents: inspect the affected tests and choose the smallest relevant check, then
run the full affected suite once before handoff. Go-only changes need the full Go
suite; dashboard-only changes need typecheck, Vitest and build. Changes to shared
Protobuf contracts need both plus generation checks. Reuse a successful run when
relevant code, dependencies, inputs and environment are unchanged, even if main has
advanced; do not repeat full suites after documentation-only follow-ups. Report
commands, exit status, skips, artifact locations and what remains unverified. Keep
failed trials. A documentation-only edit normally needs command/flag and link
verification, not a new game session. Do not mark acceptance work complete from
unit tests, compilation or native receipts alone — verify the observed outcome.

Use an isolated task worktree when peers may be active. A worktree does not inherit
the main checkout's build artifacts; run `go test`/`go build` and `pnpm install`
there for local checks.

The Python controller, `controller_tests/` and the Docker `controller-tests`/
`tests`/`worker` targets referenced by older commits and issues were removed in
G01.13 ([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)). Native
acceptance tooling is Go-only going forward; see
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38) for the current
migration slice, and `go/internal/nativeaccept/cmd/` for the actual current set of
harnesses (issue #38's own description can lag newly landed families — trust the
directory listing over the issue body when they disagree).
