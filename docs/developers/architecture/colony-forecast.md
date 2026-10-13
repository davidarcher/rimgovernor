# ColonyForecast

[Architecture](overview.md) · [Forecast facts](../contracts/forecast-contracts.md)

`PolicyFunctions.ForecastColony` is the pure owner of a baseline and a proposed
commitment delta evaluated against the same observed facts. It returns
`ColonyForecast`; it does not issue orders or certify native legality. The
first component accounts for supplied timed inventory changes. Pawn dynamics,
dependency scheduling and production selection are not yet implemented.

## Input and accounting

`ColonyForecastRequest` identifies the observed world/load scope and tick, an
explicit horizon in game days, current facts, existing commitments, a delta,
and the caller's assumptions. The world string must identify the loaded
timeline; it is provenance, not a replacement for native dispatch validation.
`ColonyForecastFactsOf` assembles the available diagnostic facts from
`RoundsFacts`. Runtime selection is connected in #3135.

A commitment identifies a Method's supplied inventory effects by stable ID.
Each effect has an exact resource, relative day and signed units, each numeric
effect/time supporting unknown. Positive units are a delivery; negative units
are a consumption. This is a conditional accounting model: delivery timing is
supplied, not a prediction of native worker completion. Unsupported effects
travel with the commitment as findings.

The delta removes IDs and adds commitments; replace by removing and adding the
same ID. An empty delta means continue. Duplicate IDs, missing removals,
negative times and nonfinite known quantities are rejected.

Both alternatives begin at the observed resource census. Known empty means
zero inventory; unknown does not. Pending delivery is never added to observed
stock. Rows are deterministic by resource, time and commitment ID. At equal
times consumption precedes delivery so output cannot pay for its own inputs.
The day-zero point is the observation; additional day-zero events are subsequent
proposed effects.

Negative balances are accounting deficits, not usable stock. A later delivery
does not erase the earlier shortfall finding. Subsequent effects remain
conditional on the caller's supplied plan: this foundation does not postpone
jobs or stop dependent output when a prerequisite fails. Consumers must examine
findings and unsupported effects, not interpret a positive final balance as
proof that a plan can execute.

Unknown effect timing makes future balances for its resource unknown; the
observed point stays intact. Unknown quantity makes balances unknown from its
known time onward. Effects beyond the horizon are reported and do not enter
the trajectory; their benefit is unassessed, not intrinsically worthless.
No daily rates, target floors or diagnostic stock needs are silently converted
to consumption events. `ResourceDemandOf` remains the shared target owner;
maximum overlapping stock floors are not additive physical consumption.

For example, start with 10 units and a build consuming 15 on day 1. A delivery
of 10 on day 2 produces a day-1 deficit of 5. Replacing only that delivery with
one on day 0.5 removes the inventory deficit while leaving observed stock at
10 in both alternatives. It says nothing yet about which pawn can deliver it.
`ColonyForecastTests` exercises this comparison and the uncertainty boundaries.

## Diagnostics and model coverage

`ColonyForecast.Observed` retains current-condition diagnostics. Its five-day
`MaintenanceHorizonDays` is independent of the requested plan horizon. The
constant still serves existing supply targets; renaming it does not change
their behavior. Proposed inventory changes do not rewrite these observations.

| Domain | Current component and meaning | Next consumer/model |
| --- | --- | --- |
| Inventory | Timed conditional balances and shortfall findings | #3127 activities; #3133 coupled transitions |
| Food | Existing eligibility/rot-aware stock runway; no future delivery credit | #3133 food production, preparation and eating |
| Power | Current network reserves at observed drain | Future infrastructure/activity effects |
| Temperature | Current sleeping extremes; current breach held over maintenance horizon | Future climate transitions |
| Defense | Current usable turret DPS and explicit gaps | #3129 qualified encounter estimates |
| Construction | Exact-resource stock/need/shortfall in units; no fabricated ETA or interchangeable stone pools | #3127 dependencies and work capacity |
| Fuel | Existing consumer burn and refill-demand diagnostic | Future scheduled supply/consumption |
| Drugs/materials/medicine | Existing realized-use runway diagnostics | Future timed activity effects |
| Animal feed | Existing herd/pasture/feed runway diagnostic | #3133 shared food accounting |
| Pawn time, mood and needs | Explicitly unsupported by this component | #3132 individual transitions; #3125 observed calibration |
| Infrastructure and dependencies | Explicitly unsupported; supplied effects are conditional | #3133 coupled model |

No diagnostic is a calibrated whole-colony trajectory. The returned unsupported
list makes the omitted effects visible even when all inventory balances are
positive. Preferences and safeguards stay separate from physical forecasts;
there is no learned weight or common scalar utility in this owner.

## Ownership and production migration

Forecasts are derived in memory from observed facts and existing saved intent.
They add no save, database table, telemetry stream or native writer. Runtime
gathers facts and sequences calls; all accounting remains pure policy.

The former C# `ProjectForward` and `ForwardInputsOf` had no C# call sites at
the audit; their types/functions are replaced rather than kept as wrappers.
The observation collector's additional facts now use `ForecastObserved`.
Independent maintenance functions remain because production supply consumes
them. The retiring Go projector belongs to #3102 and is not a second C# owner.

#3127 and #3132 provide activity and pawn state, #3133 composes the first
feedback loop, #3134 compares Method bundles and #3135 connects the result to
normal production selection through existing Rounds/Hands. The foundation
is callable and tested; it is not yet a replacement for live meal/supply
selection. No compatibility or permanent shadow layer is required.
