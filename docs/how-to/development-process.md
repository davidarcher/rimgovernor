# Develop a cohesive, typed change

[Documentation](../README.md) · [Working agreement](../../AGENTS.md)

Optimize for the time from an idea to a verified improvement in the game. Keep
changes small, contracts strict and integration continuous. Spend less time on
ceremony, speculative abstractions and redundant test runs; spend enough on types,
state ownership and recovery to make the next change easy to trust.

This is the development standard for new and changed code. It does not claim that
the existing code or CI already meets every rule. Production currently uses Python,
React/TypeScript and native C#; the [G01 rewrite](../BACKLOG.md#g01--go-controller-rewrite)
targets Go for the controller. Follow its staged acceptance gates. This guide does
not authorize an unplanned rewrite or a premature runtime switch.

## 1. Define one verifiable slice

Read the [overview](../explanation/overview.md), [source map](../reference/source-map.md),
affected contract pages, [backlog](../BACKLOG.md) and [test selection](choose-tests.md).
Inspect the implementation and existing tests before proposing another module.

Write a short task description containing:

- **Outcome:** the player-visible behavior or concrete engineering improvement.
- **Owner and path:** which existing component owns it and how inputs reach results.
- **Contracts:** types, invariants, compatibility and failure behavior that change.
- **Acceptance:** one normal example and relevant refusal, interruption or recovery cases.
- **Scope:** affected paths, dependencies and explicit exclusions.

A few sentences suffice for a small fix. For changes to subsystem ownership,
persistence or public contracts, update the relevant architecture/contract page with
the decision and rationale before expanding the implementation. No separate design
committee, mandatory agent panel or document per function is needed.

Prefer slices finishable in hours to a couple of days. A large feature can land as
several independently tested increments, with unfinished behavior explicitly gated.
Every increment must leave the default runtime usable. Keep deferred implementation
and acceptance in the backlog, including the owner/chunk and completion condition.

## 2. Establish ownership before adding code

Use the existing architecture as the integration path:

`native facts → deterministic policy or explicit player intent → shared plan → Hands → native execution → observed outcome`

Policy decides from typed facts. The plan owns durable goals and progress. Hands
owns automated mutation and reconciliation. Runtime owns lifecycle and authority.
Adapters own transport, persistence and presentation details. RimWorld owns rules
and simulation; advisers cannot issue game orders. Explicit player controls retain
their separate ownership path and invalidate pending automation.

Keep domain logic independent of transport, database, web and model SDKs. Pass
small typed inputs and consumer-owned interfaces, rather than the entire runtime
object. Construct dependencies at the application entry point. Give mutable state
one owner; make concurrency, cancellation and resource cleanup explicit. Avoid
circular imports, global service locators and hidden registration side effects.

Add a module when it has a coherent responsibility, a real caller and a clear
dependency direction. Extend the owning component for a local change. A new manager,
generic framework, service or message bus needs a concrete problem that existing
boundaries cannot solve. Prefer a modular controller process over additional services.
Do not turn a growing dispatch function into several equally coupled dispatch files.

For each new gameplay capability, account for all of these in the same slice or its
explicitly gated prerequisite increments:

- Validated observation/definition input and typed goal/action representation.
- Existing admission, resource and spatial checks; deterministic method selection.
- Registered Hands handler, durable intent and native capability/refusal handling.
- Observed postconditions, interruption, uncertain-write recovery and restoration.
- Player-facing state/errors, appropriate tests and updated source/contract docs.

For example, adding a mining action requires policy admission, resource accounting,
a guarded native handler and observed extraction/storage progress. A standalone
mining loop that writes directly to the bridge is not an integrated feature. A
receipt is not proof of mined output.

## 3. Make invalid states difficult to express

Static types are a requirement, not decoration. Changing language while retaining
unstructured payloads does not satisfy this standard.

| Surface | Standard for new and changed code |
| --- | --- |
| Go controller | Concrete structs, distinct ID types, explicit action variants and small interfaces. No `map[string]any`, reflection dispatch or unchecked type assertions in domain logic. Use validated constructors and handler-coverage tests: Go switches do not prove exhaustiveness. |
| TypeScript dashboard | Preserve `strict`; use discriminated unions and typed API models. External values enter as `unknown` and are validated. No `any`, double casts or non-null assertions to silence a missing contract. |
| Native C# | Concrete DTOs, typed operations and explicit null/error handling compatible with the installed `net472` target. No `dynamic` domain payloads or unsupported runtime dependencies. |
| Transitional Python/tooling | Annotate new/changed interfaces and use validated models, dataclasses, enums and narrow protocols. Keep dictionary decoding at boundaries; do not expand dictionary-shaped domain state or create new production Python subsystems. Apply strict checking to bounded typed surfaces as tooling lands. |

Represent unknown observations separately from known zero/false; distinguish absent
from null when the protocol does. Use integer game ticks and distinct colony, map,
load, action and direction identities. Use explicit result states for refusal,
unavailability and uncertain writes. Never turn missing evidence into success with
a default value or catch-all exception handler.

Own cross-language contracts in versioned schemas and generate applicable Go, C#
and TypeScript models as G01 brings each surface across. Keep wire models separate
from domain state. Validate bounds, required fields, variants and compatibility at
the boundary; generated types alone do not validate incoming JSON. Specify whether
unknown fields are rejected or tolerated. Unsupported mutation variants must fail
closed. Discover native definitions rather than enumerating modded game facts in code.

Generic JSON is allowed inside a narrow transport/extension decoder or retained raw
evidence, with validation before domain use. An unavoidable third-party typing escape
belongs in that adapter with a reason and a boundary test. No blanket type ignores,
disabled strict settings or silent coercion to make checks pass. Record unfinished
enforcement in the backlog instead of claiming annotations establish type safety.

## 4. Build and test in a short loop

For a bug, reproduce the externally observable failure with a focused regression
test when practical. For new logic, specify meaningful examples and invariants,
implement the smallest working slice, then refactor under passing checks. Test-first
is useful for state machines and regressions; it is not a ritual for prose or styling.

Keep many cheap deterministic checks and a smaller set of expensive native checks.
Choose tests by the boundary and risk they establish, without a fixed coverage
percentage or a mandated pyramid shape. Test behavior, not private method calls or
an expected value computed by copying the implementation.

| Change | Evidence to prioritize |
| --- | --- |
| Docs or visual styling | Verify links/commands or inspect the rendered change; use the dashboard checks for dashboard source changes. No game campaign for prose. |
| Pure policy, geometry or accounting | Fast example tests plus property/fuzz tests for conservation, valid geometry, determinism or identity where useful. Inject clocks and IDs. |
| Wire/API boundary | Decoder and consumer/producer contract tests, invalid/missing/null/overflow cases and generation drift checks. Test the real adapter wiring. |
| Persistence, authority or concurrency | Real temporary storage integration tests; fault injection around commit/dispatch; stale generations, cancellation, lost replies and restart. Run supported race checks for Go concurrency changes. |
| Native writes or pawn behavior | Relevant fixtures plus isolated native scenarios asserting actual effects and ordinary pawn work. Compilation and protocol receipts are insufficient. |
| Model interpretation | Fixed semantic cases and configured local-model acceptance when interpretation changes. Keep this separate from execution acceptance. Routine control must use zero inference. |

Mock external nondeterminism at narrow interfaces. Use real serializers, storage
transactions and handler registration in integration tests so mocks cannot hide
broken wiring. Keep fixtures minimal, sanitized and representative. For migrations,
compare captured behavior, but let documented contracts govern intentional fixes;
legacy output is not automatically correct.

During iteration run affected test files and contract neighbors. Before handoff run
the full affected suite once, following [choose checks](choose-tests.md): controller
changes need the full controller suite, dashboard changes need typecheck/tests/build,
and shared contracts, packaging or test infrastructure need both. Go gates land with
G01; do not report future checks as available today. Reuse unchanged successful
evidence and cache dependencies/build inputs. Do not rerun everything after prose edits.

Use one Docker check worker by default. Use existing scenario launchers, fresh
artifact directories and task-specific image tags. Advance native time through
`rimgovernor.native_scenario.advance_game`; interruption tests pass `expected_letters=()`.
Reuse an owned game only through the documented isolation/reset procedure. Never
swap installed DLLs while any game is running. See [local checks](local-checks.md),
[Docker checks](docker-checks.md) and [native scenarios](native-scenarios.md).

Treat a flaky test as a defect to diagnose, not a reason to retry until green. Retain
the failed evidence. Any quarantine needs a backlog item and an alternative check
for the affected contract; it cannot waive native acceptance. Report unavailable
game, platform or model coverage explicitly and leave that acceptance open.

## 5. Apply Twelve-Factor principles to this local game system

The following applies [Twelve-Factor](https://www.12factor.net/) to RimGovernor.
These are repo-specific design rules, not a claim of current full conformance.

| Factor | Application here |
| --- | --- |
| Codebase | One repository; reproducible task revisions and releases. |
| Dependencies | Declare, lock and isolate tools/libraries; record licensed game/mod inputs separately. |
| Config | Parse environment/deployment settings into validated startup configuration. |
| Backing services | Configure explicit bridge, model and storage adapters. |
| Build/release/run | Build once; identify artifacts and configuration; run without rebuilding source. |
| Processes | Persist recoverable controller state; treat memory as disposable. |
| Port binding | Serve owned endpoints on configurable loopback ports. |
| Concurrency | Parallelize isolated colonies/workers; one automated writer per colony. |
| Disposability | Bound startup/shutdown; release owned resources and preserve uncertain actions. |
| Dev/prod parity | Exercise matching packaged dependencies, contracts and native inputs. |
| Logs | Emit structured events with session/action identity and useful failure context. |
| Admin processes | Use explicit one-off maintenance commands with the same configuration/contracts. |

RimWorld is stateful. Do not pretend saves and SQLite can be discarded or put the
same colony behind interchangeable concurrent writers. Preserve paired checkpoints
and exclusive authority. Database files can be attached durable storage; this
project does not need a hosted database or distributed orchestration to comply with
the useful operational principles.

Deployment configuration includes endpoints, paths and process settings. Player
preferences, live direction, plans and game state belong in their existing durable
owners, not environment variables. Document precedence where CLI, profile and
environment inputs coexist; validate before startup and redact secrets in diagnostics.
Keep configured local LM Studio models with no silent paid-provider fallback.

Logs help diagnose; durable action records establish recovery. Keep both contracts
explicit. Destructive/offline admin tools must respect live-game ownership and stay
outside the model's tool surface. Dependency upgrades are scoped changes with
compatibility and provenance checks, not incidental additions during feature work.

## 6. Review, integrate and close the loop

Check status before editing. When peers may be active, use a separate worktree and
short-lived `codex/` task branch. Coordinate ownership of shared schemas, public
interfaces and build files. If agent work is delegated, assign bounded independent
slices with base revisions, allowed paths, contracts and acceptance; an integrator
owns the combined result. Extra agents are optional, not a required process stage.

Before committing, review the diff as the next maintainer:

- Does this solve the stated problem through the existing architecture?
- Are inputs, state transitions and ownership explicit, with no typing escape spread?
- Are failure, cancellation and recovery covered at the boundary that matters?
- Does every added abstraction have a caller and remove a concrete source of coupling?
- Do tests assert the contract, and do reported outcomes match the evidence?

Commit each completed iteration with the problem, behavior, checks and limitations.
Keep builds, databases, saves, logs and temporary scripts out of commits. Preserve
licenses and provenance. Integrate tested slices frequently rather than collecting
a large unreviewed branch. Recheck the target checkout and coordinate before landing;
never overwrite another task's edits. Push only when requested.

After integration, rerun checks when conflicts, dependencies or intervening changes
make prior evidence stale. A fast-forward of the tested revision does not itself
require a duplicate suite. Keep incomplete capabilities gated, with rollback and
removal conditions recorded in the backlog. Storage changes require compatibility
and paired-save recovery evidence; reverting code alone may not undo a migration.

A completed handoff names the branch/commit, resulting behavior, exact checks and
scope, retained evidence and remaining acceptance. Update the documentation map
for new pages and the relevant contracts when behavior changes. Remove completed
backlog work only after its acceptance is established.

Use recurring failures to improve the smallest useful check or boundary. Watch time
to a verified change, test waiting time, regressions and recovery effort; avoid
optimizing lines generated, agent count or test count. For this game, experimentation
can be inexpensive while the code remains disciplined.

## Basis for the process

Sources checked September 10, 2026. The workflow above is this repository's adaptation;
the sources are supporting guidance, not additional approval requirements.

- [DORA: working in small batches](https://dora.dev/capabilities/working-in-small-batches/)
  supports short, testable increments and frequent integration, including AI-assisted work.
- [DORA: balancing AI tensions](https://dora.dev/insights/balancing-ai-tensions/)
  discusses using AI across delivery while retaining engineering understanding.
- [DORA: test automation](https://dora.dev/capabilities/test-automation/)
  supports continuous automated feedback alongside exploratory and acceptance testing.
- [The Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)
  explains fast layered feedback and reserving expensive end-to-end checks for useful boundaries.
- [The Twelve-Factor App](https://www.12factor.net/) supplies the operational principles
  adapted above for a local controller with durable native game state.
