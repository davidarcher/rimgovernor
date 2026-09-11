# Test coverage

[Testing](README.md) · [Choose checks](choose-tests.md)

| Check | Establishes | Needs separate coverage |
| --- | --- | --- |
| Controller/dashboard fixtures | Policy, contracts, persistence and UI behavior under supplied inputs. | Actual game outcomes. |
| Native scenarios | The observed game postconditions asserted by that scenario. | Other seeds, interrupted paths and sustained operation. |
| Local-model cases | Interpretation of the supplied player requests. | Execution and completed pawn work. |
| Campaigns | Repeated behavior under recorded colony conditions and time bounds. | Broader survival conditions outside the tested matrix. |

Native receipts prove accepted orders. Construction checks must observe buildings;
production checks must observe output and consumption. The general Docker native
runner covers lifecycle, isolation, clocks, cleanup and paired checkpoint retention,
with optional rendering/input checks. Add scenario assertions for pawn outcomes.

## Run and retain native evidence

Use the [scenario launcher](scenario-launcher.md) for isolation, automatic loopback
dashboard ports, retained output and owned cleanup. Its passive observer reads the
script runtime; the scenario owns game control and assertions.

[Reusable execution cases](headless-probes.md#reuse-one-game-between-execution-cases)
restore a baseline and fresh controller state while verifying load identity, pause,
cleanup and revoked clients. A failed boundary retires the worker. Process-wide
mod state and Unity caches remain, so startup and crash recovery need fresh-process
checks.

Retain source revision, input hashes, baseline, model and objective with results.
Keep failed trials. SQLite events, action archives and logs aid diagnosis but have
bounded or missing observations. The [named native runner](native-scenarios.md)
adds failure bundles and an optional bounded timeline; it cannot reconstruct every
pawn transition or replay the simulation exactly.

See [diagnostic contracts](../contracts/diagnostics.md) and
[failure inspection](inspect-failure.md) for retained artifacts.
