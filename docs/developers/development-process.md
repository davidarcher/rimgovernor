# Development workflow

[Developer guide](README.md) · [Working agreement](../../AGENTS.md)

## Scope the change

Read the [architecture](architecture/overview.md), [source map](source-map.md),
affected contracts and [backlog](../BACKLOG.md). Identify the outcome, owning
component, changed contracts and acceptance cases. A few sentences suffice for
a small fix. Define a bounded completion criterion and the smallest sufficient
checks. Once they pass, commit and deliver. Keep incomplete capabilities gated and
record unrelated discoveries and remaining work in the backlog.

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
Manual, player direction and colony/map/load changes invalidate pending work.

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
| Python | Annotate changed interfaces; use validated models, dataclasses, enums and narrow protocols. Keep dictionary decoding at boundaries. Do not add new production Python subsystems during the Go migration. |

Distinguish unknown from zero/false, absent from null, and refusal from an uncertain
write. Use integer game ticks and distinct colony, map, load, action and direction
identities. Validate bounds and variants at entry; unsupported mutations fail closed.
Discover native definitions instead of hard-coding modded game facts.

Version shared schemas and generate language models as migration gates land.
Generated types still need runtime decoding validation. Keep unavoidable typing
escapes inside a documented adapter with a boundary test; no blanket suppressions.

## Verify and commit

Use the [testing pyramid and evidence rules](testing/choose-tests.md#testing-budget-and-evidence-reuse).
During iteration, run affected files and
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
branch and commit; push only when requested. Recheck the target checkout before
integration and rerun affected checks if conflict resolution changes tested code.

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
