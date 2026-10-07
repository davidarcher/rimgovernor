# Development workflow

[Developer guide](README.md) · [Working agreement](../../AGENTS.md)

## Scope the change

Read the [architecture](architecture/overview.md), [source map](source-map.md),
affected contracts and [backlog issues](https://github.com/davidarcher/rimgovernor/issues).
Identify the outcome, owning component, changed contracts and acceptance cases
(a few sentences suffice for a small fix). Size the slice to a coherent
milestone: game and acceptance checks are slow, so batch the related work into
one iteration. Keep incomplete capabilities gated, record unrelated discoveries
in the backlog, and continue to the next milestone of a multi-milestone task
without waiting to be re-prompted.

Check Git status before editing. Use a separate task worktree when peers may be
active, and coordinate shared interfaces. The loop is the
[working agreement](../../AGENTS.md#the-loop). Default to one agent; for a
requested team, set ownership and an integration owner once, then work
independently. Communicate actual overlaps, contract changes, blockers and ready
handoffs, not edits. Stream verified commits into `main`; inspect actual diffs
and keep test evidence across a clean integration.

## Follow the existing execution path

`native facts → policy or player intent → shared plan → Hands → native order → observed outcome`

Policy selects work; the plan owns durable concerns and progress; Hands owns
automated writes and reconciliation. Runtime owns lifecycle and authority.
Adapters handle transport, persistence and presentation. RimWorld owns
simulation. Advisers cannot issue orders or own colony invariants.

A gameplay feature needs typed observations and actions, admission and resource
checks, a registered Hands handler, observed postconditions, recovery and player
feedback; add them together or as explicitly gated prerequisites. A receipt
cannot establish completed pawn work. Observe uncertain writes before retrying.
Colony/map/load changes and tick rewinds invalidate pending work; Manual only
suspends routine concerns and their open work until control resumes.

Give mutable state one owner. Keep domain logic independent of transport,
database, web and model SDKs. Pass narrow typed inputs and interfaces; construct
dependencies at the entry point. Add a module only for a coherent responsibility
with a real caller. Make cancellation, concurrency and cleanup explicit.

## Keep contracts strict

| Surface | Standard |
| --- | --- |
| Go | Concrete structs, distinct IDs, explicit variants and small interfaces. Keep `map[string]any`, reflection dispatch and unchecked assertions out of domain logic. Test handler coverage. |
| C# | Concrete DTOs and typed operations with explicit null/error handling compatible with `net472` and installed Unity/Mono. |

Distinguish unknown from zero/false, absent from null, and refusal from an
uncertain write. Use integer game ticks and distinct colony, map, load and
action identities. Validate bounds and variants at entry; unsupported mutations
fail closed. Discover native definitions instead of hard-coding modded game
facts.

Version shared schemas and generate language models from them; generated types
still need runtime decoding validation. Keep unavoidable typing escapes inside a
documented adapter with a boundary test; no blanket suppressions.

### What is enforced and what is policy

`go run ./cmd/test` from `go/` runs the affected Go checks; `task build && task
test` from the repository root covers protobuf and C# projects. There is no
hosted CI: checks run on the developer's machine before work lands on `main`.
Race checks: see [AGENTS.md](../../AGENTS.md#checks); `task go:test:race` is an
explicit whole-module diagnostic and needs a C compiler such as WinLibs
MinGW-w64 on `PATH`. Windows race instrumentation is slow: allow at least 5 s
for unavoidable wall-clock deadlines.

[Task](https://taskfile.dev) installs with `winget install Task.Task` or
`go install github.com/go-task/task/v3/cmd/task@latest`. The root
`Taskfile.yml` pins `GOTOOLCHAIN`, `GOWORK=off` and `CGO_ENABLED=0` and includes
every project; each project directory has its own `Taskfile.yml` with `build`
(compilation plus the static gates) and `test` (tests only), so `task go:build`,
`task protobuf:build` and so on run one project. Projects needing machine-local
inputs (game managed assemblies, Harmony) report
`unavailable` naming the missing path; override with `RIMWORLD_MANAGED_DIR`
and `HARMONY_ASSEMBLY`. Outputs land under
`.rimgovernor/task/`; the protobuf exchange and native build are
checksum-skipped while inputs are unchanged (`task --force` reruns). The first
table fails the gate; the second is reviewed by hand.

| Enforced by `task build` / `task test` | Project (`Taskfile.yml`) |
| --- | --- |
| Go toolchain pinned to `go/.go-version` and the root `GOTOOLCHAIN`; gofmt; `go mod verify` and `tidy -diff`; `go vet` | `go`, `wire` (`contracts/generated/protobuf/go`), `protobuf-go` (`tools/protobuf/go`) |
| Go static analysis: unused code, always-true comparisons, dead assignments, same-type assertions, error-string style | `go` (`go tool staticcheck`, pinned in `go/go.mod`) |
| Go tests under a 60 s per-test hang guard (`-race` opt-in via `task go:test:race`); the nightly also enforces a 1 s `-short` budget and reports `slow:` skips | `go` (`checktesttimes`) |
| Generated protobuf C#/Go match the checked-in outputs; C#→Go→C# exchange is byte-identical | `protobuf` (`tools/protobuf`) |
| C# `TreatWarningsAsErrors` and `RestoreLockedMode` on Bridge, Runtime, contract probes and both fixture projects; nullable reference types on Runtime | `native`, `probes` (`contracts/tests`, ungated probes only), `fixtures` (`scripts/fixtures`) |

| Policy only (review by hand) | Notes |
| --- | --- |
| Go: no `map[string]any`, reflection dispatch or unchecked assertions in domain logic | Telemetry maps stay inside `internal/bridge` flight recording; `internal/nativeaccept` harnesses are excluded. |
| C# nullable on `RimGovernor.Bridge` and the contract probes; five Runtime persistence files carry a `#nullable disable` header | [#85](https://github.com/davidarcher/rimgovernor/issues/85) |

## Verify and commit

Use the [testing pyramid and evidence rules](testing/choose-tests.md#testing-budget-and-evidence-reuse).
Run `go run ./cmd/test` from `go/` before landing (test-only edits check their
owning package); do not follow it with a full Go suite. Reuse successful results
while the relevant source and environment are unchanged.

Test behavior at its owning boundary: pure policy with fixtures, wire formats
with real decoders, recovery with temporary storage and fault injection, native
writes with observed game outcomes. Test model interpretation separately from
execution, with configured local LM Studio models and no paid-provider fallback.

Native runs use the registered acceptance cases and the commands in the
[agent runbook](agent-runbook.md). Never replace installed DLLs while any game
is running. Keep failed evidence and report unavailable platform, model or
gameplay coverage.

Review ownership, failure handling, compatibility and unnecessary abstractions.
Commit each completed iteration with its checks and limitations; keep generated
builds, logs, saves, databases and temporary scripts out. Land with `go run
./cmd/land` from `go/` in the branch worktree (one squash commit per milestone;
see [AGENTS.md](../../AGENTS.md#the-loop)); pull requests are disabled and every
agent pushes its own landings to `origin/main`
([AGENTS.md](../../AGENTS.md#pushing-to-originmain)). The lane runs no tests and
landing needs no acceptance run: the nightly proves the affected areas, and
`acceptance run <case>` proves one earlier when warranted. `main` moving afterwards is never a
reason to rerun.

## Keep deployment and docs maintainable

Validate configuration at startup, configure endpoints explicitly and bind local
services to loopback. Pin dependencies and separate build, release and run
inputs. Keep one automated writer per colony, paired checkpoints and bounded
shutdown. Player preferences and game state belong in their durable owners, not
environment variables. Redact secrets and include session/action identity in
diagnostics.

Update the relevant player or developer page when behavior changes: short
instructions, exact contracts, useful examples. Pending work goes in the
backlog, evidence in artifacts and commits, source notices beside retained
dependencies. No change diaries or project-wide rules repeated on topic pages.
