# Testing

[Developer guide](../README.md)

Start with [test selection](choose-tests.md). Run affected files and contract
neighbors during iteration, then the full affected suite once before handoff.
For a first run without game files, use [First Docker check](first-check.md).

## Native test prerequisites

Use the [repo-local Windows/Linux input store](local-acceptance-inputs.md) before reporting a missing sandbox.

Run commands from the repository root. Use a disposable prepared profile, the
selected scenario's fixtures and matching native DLLs. Read the script's `--help`.
Use fresh `.rimgovernor/` output directories and task-specific image tags; retain
failures. Never replace installed DLLs while any RimWorld instance is running.
Docker runs use private input snapshots. Advance supervised game time through
`rimgovernor.native_scenario.advance_game`; interruption tests use `expected_letters=()`.

Report commands, exit status, skips, artifacts and unverified coverage. Native
receipts establish accepted orders; pawn outcomes need scenario assertions.

## Start and diagnose

- [Choose checks for a change](choose-tests.md)
- [First Docker check](first-check.md)
- [Run local checks](local-checks.md)
- [Run Docker controller checks](docker-checks.md)
- [Test coverage](evidence.md)
- [Inspect a failed Docker run](inspect-failure.md)

## Native runners and inputs

- [Launch an inspectable native scenario](scenario-launcher.md)
- [Run and diagnose named native scenarios](native-scenarios.md)
- [Prepare native Docker inputs](docker-inputs.md)
- [Run native Docker acceptance](docker-native.md)
- [Capture native compatibility](native-compatibility.md)
- [Run a manual Docker worker](docker-worker.md)
- [Run focused headless probes](headless-probes.md)
- [Verify native clocks and interruptions](native-clock.md)

## Gameplay

- [Verify ordinary colony establishment](crashlanded.md)
- [Verify native upkeep](upkeep-acceptance.md)
- [Verify spatial construction, access and reuse](spatial-acceptance.md)
- [Verify construction and supply recovery](construction-recovery.md)
- [Verify room refinements and cancellation](room-refinements.md)
- [Verify resource production budgets](resource-production.md)
- [Verify native material extraction](mining-acceptance.md)
- [Verify hunting screening and dispatch](hunting.md)
- [Verify animal husbandry](husbandry-acceptance.md)
- [Verify equipment upkeep in Docker](equipment-upkeep.md)
- [Verify medical care in Docker](medical-care-acceptance.md)
- [Verify mood relief in Docker](mood-relief.md)
- [Verify Go disaster planning](disaster-planning.md)
- [Verify waste hauling and burial](waste-management.md)
- [Verify native population outcomes](population-acceptance.md)
- [Verify native trades](trade-acceptance.md)
- [Verify world progression](world-progression.md)
- [Verify native forecasts](forecast-acceptance.md)

## Player actions and recovery

- [Verify semantic player commands](semantic-commands.md)
- [Verify native player actions](player-actions.md)
- [Verify durable project scheduling](project-scheduling.md)
- [Verify retained cancelled actions](cancelled-actions.md)
- [Verify checkpoint and event recovery](checkpoint-acceptance.md)
- [Audit retained plan and event evidence](audit-retention.md)
- [Verify the dashboard and video](dashboard-acceptance.md)
- [Verify visual review evidence](visual-reviews.md)

## Campaigns and performance

- [Run campaigns and measure performance](campaigns.md)
- [Measure simulation and test throughput](measure-throughput.md)
