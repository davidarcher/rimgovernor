# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` goal in
ColonyPlan. It accepts an observed native race, an optional population maximum
(`HerdPopulationMax`), native training targets and explicit permission to
slaughter surplus (`RoutinePolicy.AllowSlaughter`). Slaughter defaults off. A
population target alone does not authorize killing animals. There is no
per-race protected-ID list, breeder-pair reserve or feed-reserve bookkeeping:
surplus eligibility relies on native's own `SafeToSlaughter` fact.

## Ownership and population

The goal belongs to the colony and map where it was accepted. Each native request
also binds the load token, exact animal ID, animal settings token and herd census.
Hands previews and dispatches through the normal writer lock and direction guards.
The native callback requires a paused matching context and rejects changed settings
or population. Unconfirmed requests remain blocked for inspection, without retry.

Surplus selection subtracts existing slaughter/release designations. Native
slaughter eligibility is checked again at dispatch; bonded, mastered, pregnant,
downed and released animals are excluded by RimWorld's own rules. Masters,
allowed areas, following, sterilization and breeding separation are ordinary
game settings: while native authority reads Auto the controller may change any
of them, including ones the player just set (see the working agreement). Today
the husbandry methods are only `train` and `slaughter`, so it does not yet
write them; that is missing coverage, not a hands-off rule.

Below-minimum populations wait for ordinary births when a pregnancy or fertile pair
is observed. Otherwise the goal reports a missing breeding prerequisite. Pregnancy
and mating eligibility never count as new animals. Renewing a target cancels its
uncompleted controller steps; cancellation leaves previously issued game orders in
place, following the shared goal cancellation contract.

## Training and products

Training requests use native `CanAssignToTrain` and `SetWantedRecursive`; native
`learned` and step counts track progress separately from settings receipts. The
per-animal settings token and herd census token are compare-and-swap guards
against stale in-flight snapshots: they are re-read immediately before preview
and dispatch, and a mismatch (player edit, birth, death) fails that attempt so
the next cycle plans from fresh state. They never block the controller from
changing an animal again. Removal-designated animals receive no training changes.

Handling joins shared deterministic work allocation with the observed native minimum
skill. Player work overrides remain authoritative. The herd observation retains safe
handler reachability, current priorities, jobs and targets, alongside training target
count and ready product count. Normal native handlers perform training, milking and
shearing. No instant training, forced product generation or alternative job executor
exists. A full udder or wool comp is pending work, not a collected product.

## Feed capacity and containment

Reserve days are a planning horizon supplied by the player, including seasonal needs.
The shared food forecast allocates current stored food among native eligible eaters
using diet, policy, safe reachability, held stock and rot deadlines. Missing reads,
grass, expected harvest and unborn animals cannot certify feed capacity. Animal pen
membership uses the native enclosed, suitable pen lookup; a marker by itself is not
containment. Area-managed animals use their current allowed-area membership.

An explicit feed resource, or observed feed used exclusively by animals, creates an
owned `MaintainResource` goal through the existing acquisition/production methods.
The stock target uses native per-item nutrition and demand from competing eligible
eaters. Missing suitable feed, production prerequisites, handlers, access or storage
remain visible blockers. Cancelling the herd stops its unissued feed work. Acquiring
stock does not establish that animals can reach or have consumed it.

These are current-condition projections. Future births, changing temperatures,
spoilage and native job selection require fresh review; a bounded acceptance run
does not establish indefinite herd sustainability.

See husbandry acceptance for the native fixture
and the distinction between setup, orders and pawn outcomes.
