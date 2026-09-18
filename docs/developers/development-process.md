# Development workflow

[Developer guide](README.md) · [Working agreement](../../AGENTS.md)

## Scope the change

Read the [architecture](architecture/overview.md), [source map](source-map.md),
affected contracts and [backlog issues](https://github.com/davidarcher/rimgovernor/issues). Identify the outcome, owning
component, changed contracts and acceptance cases. A few sentences suffice for
a small fix. Size the slice to a coherent milestone, not the smallest possible
step: game and acceptance checks are slow, so batch the related work a milestone
needs into one iteration rather than stopping after each small increment. Once
checks pass, commit and deliver. Keep incomplete capabilities gated and record
unrelated discoveries and remaining work in the backlog. When a task spans
multiple milestones, continue to the next one instead of stopping to be re-prompted.

Check Git status before editing. Use a separate task worktree and `codex/` branch
when peers may be active. Coordinate shared interfaces and integration.

Follow the [delivery and coordination rules](../../AGENTS.md#delivery-speed-and-coordination).
Default to one agent. For requested teams, establish ownership and an integration
owner once, then work independently. Communicate actual overlaps, contract changes,
blockers and ready handoffs; do not narrate edits or seek speculative conflict checks.
When landing is authorized, stream verified commits into main without waiting for
unrelated teams. Inspect actual diffs and preserve applicable test evidence across
clean integration; a new main HEAD alone is not a reason to rerun tests.

## Follow the existing execution path

`native facts → policy or player intent → shared plan → Hands → native order → observed outcome`

Policy selects work; the plan owns durable goals and progress; Hands owns
automated writes and reconciliation. Runtime owns lifecycle and authority.
Adapters handle transport, persistence and presentation. RimWorld owns simulation.
Advisers cannot issue orders or own colony invariants.

A gameplay feature needs typed observations and actions, admission and resource
checks, a registered Hands handler, observed postconditions, recovery and player
feedback. Add these together or as explicitly gated prerequisites. A receipt
cannot establish completed pawn work. Observe uncertain writes before retrying.
Colony/map/load changes and tick rewinds invalidate pending work; Manual only
suspends routine goals and their open work until control resumes.

Give mutable state one owner. Keep domain logic independent of transport, database,
web and model SDKs. Pass narrow typed inputs and interfaces; construct dependencies
at the entry point. Add a module only for a coherent responsibility with a real caller.
Make cancellation, concurrency and resource cleanup explicit.

## Keep contracts strict

| Surface | Standard |
| --- | --- |
| Go | Concrete structs, distinct IDs, explicit variants and small interfaces. Keep `map[string]any`, reflection dispatch and unchecked assertions out of domain logic. Test handler coverage. |
| TypeScript | Keep `strict`; use discriminated unions and typed API models. Validate external `unknown` values. Avoid `any`, double casts and assertions that hide missing contracts. |
| C# | Concrete DTOs and typed operations with explicit null/error handling compatible with `net472` and installed Unity/Mono. |

Distinguish unknown from zero/false, absent from null, and refusal from an uncertain
write. Use integer game ticks and distinct colony, map, load and action
identities. Validate bounds and variants at entry; unsupported mutations fail closed.
Discover native definitions instead of hard-coding modded game facts.

Version shared schemas and generate language models from them.
Generated types still need runtime decoding validation. Keep unavoidable typing
escapes inside a documented adapter with a boundary test; no blanket suppressions.

### What is enforced and what is policy

`task build && task test` from the repository root is the gate. There is no
hosted CI: the gate runs on the developer's machine before work lands on
`main`, and `go:test` runs the `-race` pass alongside the plain pass whenever a
C compiler (`gcc`, e.g. WinLibs MinGW-w64 via `winget`) is on `PATH`. The race
runtime is several times slower on Windows than on Linux, so test deadlines
that gate on wall-clock time allow at least 5s.
[Task](https://taskfile.dev) installs with `winget install Task.Task` or
`go install github.com/go-task/task/v3/cmd/task@latest`. The root
`Taskfile.yml` pins `GOTOOLCHAIN`, `GOWORK=off` and `CGO_ENABLED=0` and
includes every project; each project directory ships its own `Taskfile.yml`
with `build` (compilation plus the static gates that fail a build) and `test`
(tests only), so `task go:build`, `task dashboard:test` and so on run one
project. Projects with nothing to test say so. Projects that need
machine-local inputs (the game's managed assemblies, Harmony, the
RimBridgeServer SDK) report `unavailable` naming the missing path instead of
passing; override the defaults with `RIMWORLD_MANAGED_DIR`, `HARMONY_ASSEMBLY`
and `RIMBRIDGE_SDK_DIR`. Outputs and evidence land under `.rimgovernor/task/`;
the protobuf exchange and the native build are checksum-skipped while their
inputs are unchanged (`task --force` reruns them). Everything in the first
table fails the gate; everything in the second is reviewed by hand.

| Enforced by `task build` / `task test` | Project (`Taskfile.yml`) |
| --- | --- |
| Go toolchain pinned to `go/.go-version` and the root `GOTOOLCHAIN`; gofmt; `go mod verify` and `tidy -diff`; `go vet` | `go`, `wire` (`contracts/generated/protobuf/go`), `protobuf-go` (`tools/protobuf/go`) |
| Go static analysis: unused code, always-true comparisons, dead assignments, same-type assertions, error-string style | `go` (`go tool staticcheck`, pinned in `go/go.mod`) |
| Go tests under a 10 s per-test budget; `-race` when a C compiler is present | `go` (`checktesttimes`, `go test -race`) |
| TypeScript `strict`; no `any`, `@ts-ignore`, `@ts-nocheck`, unsafe `any` flow, unnecessary or object-literal assertions, or `as unknown as` double casts; `@ts-expect-error` only with a description | `dashboard` (`dashboard/eslint.config.js`, typescript-eslint type-checked, plus `tsc --noEmit`) |
| Dashboard dependencies locked | `dashboard` (`pnpm install --frozen-lockfile`) |
| Generated protobuf C#/Go match the checked-in outputs; C#→Go→C# exchange is byte-identical | `protobuf` (`tools/protobuf`) |
| C# `TreatWarningsAsErrors` and `RestoreLockedMode` on Bridge, Runtime, contract probes and both fixture projects; nullable reference types on Runtime | `native`, `probes` (`contracts/tests`, ungated probes only), `fixtures` (`scripts/fixtures`) |

| Policy only (review by hand) | Notes |
| --- | --- |
| Go: no `map[string]any`, reflection dispatch or unchecked assertions in domain logic | Telemetry maps stay inside `internal/bridge` flight recording; `internal/nativeaccept` harnesses are the excluded legacy path. |
| C# nullable on `RimGovernor.Bridge` and the contract probes; five Runtime persistence files carry a `#nullable disable` header | [#85](https://github.com/davidarcher/rimgovernor/issues/85) |

## Verify and commit

Use the [testing pyramid and evidence rules](testing/choose-tests.md#testing-budget-and-evidence-reuse).
Run `task build && task test` (or just the projects the change touches,
`task go:test`) before landing. During iteration, run affected files and
contract neighbors. Before handoff, run the full affected suite once. Reuse
successful results when the relevant source and environment are unchanged.

Test behavior at its owning boundary: pure policy with fixtures, wire formats
with real decoders, recovery with temporary storage and fault injection, native
writes with observed game outcomes. Test model interpretation separately from
execution. Use configured local LM Studio models with no paid-provider fallback.

Native runs use existing launchers, fresh outputs and task-specific image tags.
Advance time with `rimgovernor.native_scenario.advance_game`; interruption tests
pass `expected_letters=()`. Never replace installed DLLs while any game is running.
Keep failed evidence and report unavailable platform, model or gameplay coverage.

Review ownership, failure handling, compatibility and unnecessary abstractions.
Commit each completed iteration with its checks and limitations. Keep generated
builds, logs, saves, databases and temporary scripts out of commits. Report the
branch and commit. Land authorized work on local `main` through the landing
lane, `go run ./cmd/land` from `go/` in the branch's worktree (one squash
commit per milestone; the loop is in [AGENTS.md](../../AGENTS.md), the
machine setup in the [agent runbook](agent-runbook.md)); pull requests are
disabled and the maintainer pushes `main` manually. `go run ./cmd/test`
from `go/` is the test loop and the pre-land check (the lane runs no
tests); run the acceptance harnesses it names at the milestone, at most
once per milestone. `main` moving afterwards is never a reason to rerun.

## Keep deployment and docs maintainable

Validate configuration at startup, configure endpoints explicitly and bind local
services to loopback. Pin dependencies and separate build, release and run inputs.
Keep one automated writer per colony, paired checkpoints and bounded shutdown.
Player preferences and game state belong in their durable owners, not environment
variables. Redact secrets and include session/action identity in diagnostics.

Update the relevant player or developer page when behavior changes. Prefer short
instructions, exact contracts and useful examples. Put pending work in the backlog,
verification evidence in artifacts and commits, and source notices beside retained
dependencies. Avoid change diaries and repeated project-wide rules on topic pages.
