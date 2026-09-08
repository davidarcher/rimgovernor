# Native bridge migration — current status

Updated 2026-09-08. RimBridge/GABS is the sole backend; RIMAPI replacement is
complete. The remaining work is capability parity and gameplay acceptance.
Chronological implementation notes, including superseded next steps, are in
[the archived history](archive/bridge/RIMBRIDGE_MIGRATION_HISTORY_20260908.md).
The [20-run campaign](STARTER_CAMPAIGN_20260908.md) finished with no combined
starter foothold; five stockpiles and one completed small room shell were partial
successes. That evidence determines the ordering below.

## Current priorities

1. **Native execution acceptance.** [Schema-bound commitments](EXECUTION_CONTRACTS.md)
   are implemented and passed a real Qwen pawn-setting/readback test while paused.
   This also fixed PawnConfig rejecting the load IDs returned by ListPawns.
   Test allowing nearby supplies, setting work priorities and issuing a bill
   independently before another broad startup campaign. These gameplay cases
   remain unproven; valid argument structure alone is not a useful strategy.
2. **Complete modal control and pause ownership.** Native letter reads contain
   full text. UI state and screen-target inspection plus exact-window dismissal
   are connected, schema-bound and verified by fresh native readback. A headless
   options-dialog fixture passed while preserving pause; the baseline had no
   letters, so letter-dialog recovery remains untested. General quest choices and
   AI-owned modal recovery/resumption remain unfinished: force-pause still switches
   to Manual, preventing autonomous cleanup until Automate is explicitly restored.
   Distinguish AI-opened modals from player pauses before enabling that recovery.
   Do not automatically dismiss all dialogs. See `scripts/dialog_smoke.py`.
3. **Autonomous eight-tribal starter foothold.** Repeatable headless campaigns now
   exist; small real-model rooms/stockpiles have worked, but the combined starter
   check remains unproven. Test supply access, eight sleeping places and storage,
   then food production and multi-day survival. Queued orders are not completion.
4. **Spatial architect parity.** Restore long-term layout, shared reservations,
   entrances/access, room roles and staged execution. Room-shell compilation,
   geometry checks and native preflight exist; the richer retired architect does
   not. Current VL second opinions are optional and failed missing-door acceptance.
5. **Native food forecasts.** Supply units are exposed, but nutrition, consumption,
   spoilage, expected harvest and animal feed forecasts remain incomplete. Use
   game definitions, not a fixed food table. Basic starting orders should not wait
   for these advanced forecasts.
6. **Combat/rescue gameplay acceptance.** Movement, equip, observed hits, tending
   and AI draft cleanup passed controlled tests. Actual raid victory, autonomous
   tactics, real carry-to-bed rescue and model-selected triage remain unproven.
7. **Trade acceptance.** Native session/staging/preview/execution exists. Verify a
   real buy/sell exchange, silver and stock deltas, stale sessions and delivery.
8. **Remaining player actions and inspectors.** Captured native UI controls,
   compact companion UI reports and exact naming-input writes are connected.
   Options-dialog activation and naming-field readback passed live fixtures;
   quest acceptance and final rename effects still need scenario tests.
   Packed-furniture installation and richer building/bill/zone projections remain.
   Native message/alert culprit and inspect-tab reads passed a headless fixture.
9. **Visual reviewer UX.** Near/wide framing, yielding to player camera control,
   source-image concern overlays, and cleaner good/bad layout acceptance fixtures.
10. **Linux/container workers and throughput.** Two Windows headless instances
    passed independent-clock and peer-survival tests. Parallel model throughput
    remains unmeasured. Parameterize Linux game/GABS paths, inference networking,
    profiles and artifact persistence before cloud deployment. See
    [headless campaigns](HEADLESS_CAMPAIGNS.md).

## Implemented — do not re-queue

- Native transport, discovered schemas, typed strategist inspection tools and
  read-only previews. Game writes go through plans and deterministic execution.
- Native construction, zones, bills, pawn orders/settings, research, world and
  letter inspection; letter open/dismiss does not imply quest acceptance.
- Durable plans, cancellation, basic project deduplication/reconciliation, native
  colony/load identity, saved memory and the player notebook.
- Strategy-card and wiki retrieval, optional generic data scout and visual reviewer.
- Native clock leases, event stops, draft ownership, headless startup, demand-driven
  rendering and throughput measurements. No RIMAPI video dependency remains.
- Integrated dashboard, chat/steering, projects, people, activity and diagnostics.
- Native construction preflight, compact cell encoding, catalog pagination,
  structured-response recovery, initial roster/supply context, and exact
  review-local evidence retention across compaction.
- Incremental commitments, whole-category discovery indexes, schema grounding of
  discovered construction names, native-legal blueprints before material arrival,
  bounded context calibration, and retryable construction observation failures.
- Persistent urgent conditions are revisited after unrelated decisions; startup
  guidance is surfaced without requiring a knowledge lookup first.

## Acceptance boundaries

Unit/protocol tests are not autonomous gameplay. Real scripted tests prove native
execution but do not prove model strategy. Recent real-model runs produced some
actual construction, but infrastructure changes must not be reported as colony
success.

Campaigns use the same eight-tribal baseline and local model, fresh runtime state
per iteration, headless rendering and fast ordinary simulation. Preserve model/
tool counts, refusals, actual orders and native completion, including failed runs.
Investigate GABS's observed Windows runtime-state rename failure independently;
read failures no longer permanently invalidate issued construction.

See [construction preflight](CONSTRUCTION_PREFLIGHT.md),
[typed tools](TYPED_NATIVE_INSPECTIONS.md), [review evidence](REVIEW_EVIDENCE.md),
and [native control acceptance](NATIVE_CONTROL_CHECKPOINT.md). The instruments
audit and control checkpoint contain dated history; this document is the current
queue. Persistent survival, winter readiness and a successful full game remain
later milestones.
