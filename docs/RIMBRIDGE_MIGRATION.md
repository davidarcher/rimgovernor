# Native bridge migration — current status

Updated 2026-09-08. RimBridge/GABS is the sole backend; RIMAPI replacement is
complete. The remaining work is capability parity and gameplay acceptance.
Chronological implementation notes, including superseded next steps, are in
[the archived history](archive/bridge/RIMBRIDGE_MIGRATION_HISTORY_20260908.md).

## Current priorities

1. **Autonomous eight-tribal starter foothold.** Scripted construction, instant
   zones/spots, equipment and native readback work. Real Qwen runs still mostly
   inspect without committing; accepted plans have also used invented definitions.
   Run up to 20 fresh headless experiments, record results, and fix observed
   failures. Usable starting orders are not the same as long-term survival.
2. **Spatial architect parity.** Restore long-term layout, shared reservations,
   entrances/access, room roles and staged execution. Room-shell compilation,
   geometry checks and native preflight exist; the richer retired architect does
   not. Current VL second opinions are optional and failed missing-door acceptance.
3. **Native food forecasts.** Supply units are exposed, but nutrition, consumption,
   spoilage, expected harvest and animal feed forecasts remain incomplete. Use
   game definitions, not a fixed food table. Basic starting orders should not wait
   for these advanced forecasts.
4. **Combat/rescue gameplay acceptance.** Movement, equip, observed hits, tending
   and AI draft cleanup passed controlled tests. Actual raid victory, autonomous
   tactics, real carry-to-bed rescue and model-selected triage remain unproven.
5. **Trade acceptance.** Native session/staging/preview/execution exists. Verify a
   real buy/sell exchange, silver and stock deltas, stale sessions and delivery.
6. **Remaining player actions and inspectors.** Quest/dialog choices, verified UI
   fallbacks, packed-furniture installation, and richer building/bill/zone details.
7. **Visual reviewer UX.** Near/wide framing, yielding to player camera control,
   source-image concern overlays, and cleaner good/bad layout acceptance fixtures.

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

## Acceptance boundaries

Unit/protocol tests are not autonomous gameplay. Real scripted tests prove native
execution but do not prove model strategy. Recent real-model probes produced zero
orders; infrastructure changes must not be reported as colony success.

The next experiment uses the same eight-tribal baseline and local model, fresh
runtime state per iteration, headless rendering and fast ordinary simulation.
Record model/tool counts, refusals, actual orders and native completion. Stop at
a verified starter foothold or 20 iterations; preserve failed evidence as well.

See [construction preflight](CONSTRUCTION_PREFLIGHT.md),
[typed tools](TYPED_NATIVE_INSPECTIONS.md), [review evidence](REVIEW_EVIDENCE.md),
and [native control acceptance](NATIVE_CONTROL_CHECKPOINT.md). The instruments
audit and control checkpoint contain dated history; this document is the current
queue. Persistent survival, winter readiness and a successful full game remain
later milestones.
