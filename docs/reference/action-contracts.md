# Action completion contracts

[Documentation](../README.md)

An accepted native order and a completed action are separate states. The table defines
what each action must observe.

| Committed action | Completion boundary |
| --- | --- |
| `build_room_shell`, `place_buildings` | Native building observations through ProjectBook; a shell does not certify roofing or usable shelter. |
| `create_zone` | Validated native zone geometry/readback; storage and crop production are separate outcomes. |
| `native_operation` | Schema-validated native receipt/readback by default. Bills, settings, designators and UI actions do not imply downstream pawn labor finished. |
| `home/install` through `native_operation` | Exact inner building identity at the intended destination/rotation. |
| Medical native operations | Optional `patient_tended` and `patient_in_bed` wait for fresh living-patient observations. These certify current treatment/delivery state, not full healing or actor attribution. |
| `trade` | Guarded open/stage/preview/accept with participant, content and silver-budget checks; hauling/storage remain separate. |
| `clock`, `stand_down` | Native clock control or verified release of selected current-load AI-owned drafts; neither certifies combat victory. |

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
