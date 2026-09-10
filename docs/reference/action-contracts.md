# Action completion contracts

[Documentation](../README.md)

An accepted native order and a completed action are separate states. The table defines
what each action must observe.

| Committed action | Completion boundary |
| --- | --- |
| `build_room_shell`, `place_buildings` | Native building observations through ProjectBook; a shell does not certify roofing or usable shelter. |
| `create_zone` | Validated native zone geometry/readback; storage and crop production are separate outcomes. |
| `native_operation` | Schema-validated native receipt/readback by default. Bills, settings, designators and UI actions do not imply downstream pawn labor finished. |
| Existing-zone edits | Fresh native geometry, crop or filter must match the requested native edit; deleted zones must be absent. |
| Bill ingredient whitelists | Exact native bill identity and filter must match fresh readback; production remains separate. |
| `home/install` through `native_operation` | Exact inner building identity at the intended destination/rotation. |
| Medical native operations | Optional `patient_tended` and `patient_in_bed` wait for fresh living-patient observations. These certify current treatment/delivery state, not full healing or actor attribution. |
| Waste native operations | Required `waste_contained` verifies the exact item in separated storage or a grave on a later native tick. Relocation does not mean destruction; explicit burial requires the body inside a grave. |
| `home/gear_upkeep` | `pawn_gear` requires fresh exact apparel/primary-weapon identity on the assigned pawn; an ordered job is insufficient. See [equipment upkeep](equipment-upkeep.md). |
| Population native operations | Orders/settings have receipt boundaries; [population goals](population-contracts.md) separately observe custody, care, recruitment and work/equipment/housing integration. Current-load observed custody/settings can resolve an uncertain order without replaying it. |

| `RequestSurgery` | `surgery_health` verifies the expected native condition change on the exact patient/body part. Bill removal does not certify success; postoperative recovery remains separate. See [medical care](medical-care.md). |
| Upkeep native operations | `upkeep_target` waits for protected stock, repaired target health, or cleaned target absence. Ownership, quantity and unknown-state rules are in [upkeep contracts](upkeep-contracts.md). |
| `trade` | Guarded open/stage/preview/accept with participant, content and silver-budget checks; hauling/storage remain separate. |
| Caravan formation and travel | Exact living crew with observed loaded departure, destination arrival or home-map return; assembly and route receipts alone do not complete travel. |
| Quest acceptance | Fresh scoped acceptance tick; native quest success remains a separate observed state. |
| `clock`, `stand_down` | Native clock control or verified release of selected current-load AI-owned drafts; neither certifies combat victory. |

## Trades

Map trades require a paused game and a reachable, eligible negotiator adjacent to
the trader. Native sessions bind the exact deal, participants, map and colony load.
Set/cancel/accept requests carry the session ID; acceptance also carries the preview
signature covering exact rows, counts, stock identities and prices. Native acceptance
rechecks stock eligibility, both silver balances and trader availability after any
viewing delay. A lost receipt retains the shared Hands uncertain-write marker across
restore and cannot replay. Bought map goods appear at the carrying trader/pack
animal's native delivery location. The caravan lord prevents its own pawns from
retrieving them; this does not forbid the colony from using them. Exchange
completion does not certify storage.

Policy trades select bounded purchases and surplus sales from the fresh native
sheet. `economicFloors` on native acceptance contains exact `Def=count` entries
separated by semicolons, including Silver. Acceptance checks remaining actual
stack counts in the same main-thread operation as the exchange and refuses
protected exports. Unknown or truncated inventory prevents selection. A policy
with no eligible affordable lines cancels its own session without an exchange;
the retained policy evidence explains each target. Neither cancellation nor
acceptance takes a trader quest. See [economic command fields](command-contracts.md).

Direct orbital opening is refused. Ordinary orbital input requires the comms
console's native menu, a powered reachable interaction cell and capable negotiator,
then `UseCommsConsole` and the native trade dialog. Beacon stock eligibility and
drop-pod delivery belong to that native path; adjacent map-trade checks cannot
authorize it.

## Dependencies and retained cancellation

Dependencies distinguish orders issued from work complete. Stable step identities retain
receipts; changed intent requires a new identity. Duplicate intent and cancelled
fingerprints prevent recreating the same work under another ID. An unchanged cancelled
step can remain in subsequent plan revisions with its receipts intact; it is never ready
for execution. Changing or reintroducing that cancelled work is rejected. Cancelling a
plan step does not cancel existing game orders. Observation failures retain issued work
until fresh evidence arrives. Ambiguous non-idempotent writes block for inspection
instead of automatic replay; only explicitly retryable failures can be retried through
the plan.

## Related reading

See [spatial contracts](spatial-contracts.md), [recovery
contracts](recovery-contracts.md), and [plans and
Hands](../explanation/plans-and-hands.md).
