# Recovery and uncertain-write contracts

[Documentation](../README.md)

Recovery operates on the existing action identity and requires fresh evidence. Unknown
write outcomes must be inspected before any further action.

## Interrupted treatment

Confirmed interrupted autonomous treatment has bounded recovery on the same action
identity. Under the runtime writer lock, fresh patient/doctor observations and the
native tick must match the issued load and current direction. Completed or resumed
treatment is observed without another order. A replacement archives the prior
receipt/failure in durable action recovery history, clears only that attempt's issued
slots, and passes through normal Hands preview, validation and postcondition tracking
again. The existing method-attempt limit bounds replacements. Unknown/legacy receipt
scope, save rewinds, competing treatment, incapable doctors and player overrides retain
an explicit hold. Goal recovery evidence and reasons are shared with chat and the
Autopilot panel. Medical priorities include native tending needs even without bleeding.

New treatment methods rank observed bleeding deadlines, then downed patients and
stable pawn identities. Capable doctors are ranked by Medicine skill and must pass
native tending previews; at most eight refused pairs are inspected per method.
Existing tending retains its job and supervised simulation time. Player-disabled
doctors and externally drafted pawns are excluded. Confirmed treatment receipts
retain the player-direction generation: interrupted treatment after changed or
unknown direction stays held, while completed/resumed treatment can be observed
without another order.

## Construction recovery

An autonomous construction action can recover from a known pre-write material shortage
or placement refusal on the same action identity. Unknown cost/stock observations,
native write failures and policy refusals are not classified as resource shortages.
Under the writer lock, recovery verifies the saved load, direction, action signature and
tick, then previews every unissued placement against current resources, reservations and
geometry. Confirmed slots are retained; any uncertain slot prevents automatic recovery.
Resumption history survives persistence, and the method-attempt limit bounds successful
resumptions. Hands rechecks each placement before writing; a recovered reservation does
not certify construction. Placement refusals require explicitly recorded pre-write
scope; legacy refusals without that evidence remain blocked even if a later preview
succeeds. Recovery records the persisted player-direction generation separately from the
review revision: internal failure/observation events invalidate in-flight reviews
without impersonating a new player instruction. Fresh preview transactions still guard
the current review revision, plan, mode and load. Their read-only previews reuse the
held writer lock and do not require the review to be marked finished.

## Starting-supply recovery

A refused autonomous starter-stock allow preview can complete through observation when
fresh native counts prove no forbidden or fogged stock remains around its target. This
reconciliation performs no write and retains the refusal and native readback in recovery
history. Identity and buffered clock events are refreshed before the final mode,
player-direction, plan and action guards.

## Read retries and thermal observations

The observed GABS runtime-state publication fault permits two bounded retries for
approved reads and explicit previews. Mutations and mixed-operation defaults do not use
these retries. Model inspection reports retain native scope notes, and unavailable power
observations cannot clear an established reserve-risk signal. Selective native building
reports include cooler intake/exhaust and vent front/back cells for current rotation,
including intended blueprint/frame geometry. Fogged or out-of-bounds cell state remains
unknown. Unsupported custom thermal classes remain unknown; geometry alone does not
certify cooling or usable rooms.
