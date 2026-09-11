# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` goal in
ColonyPlan. It accepts an observed native race, population minimum/maximum, stored
feed reserve days, optional feed resource, native training targets, protected animal
IDs, breeder-pair reserve and explicit permission to slaughter surplus. Slaughter
defaults off. A population target alone does not authorize killing animals.

## Ownership and population

The goal belongs to the colony and map where it was accepted. Each native request
also binds the load token, exact animal ID, animal settings token and herd census.
Hands previews and dispatches through the normal writer lock and direction guards.
The native callback requires a paused matching context and rejects changed settings
or population. Unconfirmed requests remain blocked for inspection, without retry.

Surplus selection subtracts existing slaughter/release designations and preserves
the requested number of fertile adult males and females. Native slaughter eligibility
is checked again at dispatch; bonded, mastered, pregnant, downed, released and
explicitly protected animals are excluded. The controller preserves native masters,
areas, following, sterilization and player breeding separation. It never forces
mating or removes those restrictions to meet a population target.

Below-minimum populations wait for ordinary births when a pregnancy or fertile pair
is observed. Otherwise the goal reports a missing breeding prerequisite. Pregnancy
and mating eligibility never count as new animals. Renewing a target cancels its
uncompleted controller steps; cancellation leaves previously issued game orders in
place, following the shared goal cancellation contract.

## Training and products

Training requests use native `CanAssignToTrain` and `SetWantedRecursive`; native
`learned` and step counts track progress separately from settings receipts. Once the
controller changes a setting, it retains the returned settings token. Later player
changes require explicit target renewal before the controller can change that animal
again. Protected or removal-designated animals receive no training changes.

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

See [husbandry acceptance](../testing/husbandry-acceptance.md) for the native fixture
and the distinction between setup, orders and pawn outcomes.
