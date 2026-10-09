# Native rules

[Contracts](README.md) · [wire contract](../../../contracts/proto/operations.proto) (`Rules`, `Rule`)

A native rule is "on trigger X, if predicates P, do whitelisted action A": Go
authors it, native executes it in process, so a reaction needs no Go round trip.
Go keeps the policy. Native never decides what to do beyond the rule it was given.

## v1 rule

| Part | v1 |
|---|---|
| Trigger | `PREY_KILLED`: a player pawn killed a wild animal. Fired from the kill hook the [delivery ledger](forecast-contracts.md#delivery-ledger) shares (`Pawn.Kill`); no ring is read. The actor is the killer. |
| Predicates | `ACTOR_UNDRAFTED`, `ACTOR_HUNTING_WORK_ACTIVE`, `TARGET_AVAILABLE` (the selector's own search: no target, no firing). |
| Action | `GIVE_JOB` `Hunt` on `NEAREST_DESIGNATED_PREY` within `radius` cells (1 to 100): a hunt-designated wild animal the actor can reach by a route clear of predators and that a work giver the actor may do builds the job on. Built and taken by the path `GiveJobIntent`'s prioritized arm uses (`NativePrioritizedJob.TryTake`), as a player-forced order. |
| Not in v1 | Draft and undraft (drafts stay plan-owned, `buildingruntime/draft_needs.go`), any other job, any other trigger. A rule outside the whitelist is refused with a reason. |

The firing runs at the end of the tick of the kill, never inside the kill, so the hunter's own
job is not replaced mid-shot. Its effect is the hunter moving on to the next prey instead of
hauling its own kill; the corpse is left for the ordinary haul.

## Ops

- `rules_attach`: replace every active rule with the accepted ones (idempotent by id) and set the lease
  `expires_at_tick`. The reply lists accepted ids and a refusal reason per refused rule (invalid or
  duplicate id, more than 16 rules, an unsupported trigger, predicate, action, job or target, a bad
  radius). A lease not after the current tick is a failure.
- `rules_clear`: deactivate every rule. Needs no authority.
- `rules_read_status`: active rules with their firing count and last firing, the lease tick and the
  ticks remaining, and `lease_expired` from the expiry until the next attach or clear.

Go re-attaches on the rules planner cadence with a lease (2500 ticks); rules live in native memory only and a load
starts with none. The controller attaches through the `rules_attach` action (`RulesAttachIntent` on
Actions/Apply, [action contracts](action-contracts.md)), which carries the rules and a lease relative to
the apply tick and sits in the session journal before native is written. `RoundsRulesPlanner`
(family `rules`, every half lease) derives the set from the hunt plan with `policy.HuntChainRules` (a Hunting-capable ranged colonist and at least one designated prey, including prey held from new acquisition) and
commits one method per Round under `EnsureFoodSupply`. Both retained and fresh
reads include the pawn frame that supplies hunter profiles; an invalidated census
therefore renews from fresh evidence. Truly unread acquisition or hunter facts
leave the previous lease unchanged, so loss of evidence still expires naturally; an empty set clears what an earlier Round attached,
and a Standard that is no longer workable lets the lease lapse. Admission holds for new hunt designations do not clear existing orders; native checks route and work-giver legality when selecting the next prey and fires nothing after the last kill. The `rules_attach` op (absolute
`expires_at_tick`) stays for harnesses.

## Safety

- Inert unless native holds Auto authority (`authority_read_status`): a firing runs in an owned
  authority scope, so a revoked authority fires nothing and the write is not read as a player order.
- At most 16 rules; one firing per actor per 60 ticks across all rules.
- Lease: when the game tick reaches `expires_at_tick`, native deactivates every rule without Go and
  emits one `rule_lease_expired` event.
- Journal before write: native appends `rule_fired` (rule id, job, radius, actor, target, tick) to the
  [clock event ring](../../../contracts/proto/clock-lifecycle.md) and writes the game only once the row is
  in the journal. Both rule events are epoch-less, so they carry no owner; Go ingests them through the
  clock inbox, writes a [`rule` flight row](flight-rows.md) for each and they never hold a review.

Source: `NativeRuleBook.cs` (validation, lease, limits; no game types, probed by
`contracts/tests/NativeContractProbes` `native-rule-runtime`), `NativeRuleRuntime.cs` (trigger, target
search, journal, write), `NativeRuleTools.cs` (ops). Acceptance: `food/hunt-chain-rule` records two completed controller attachment
ticks, requires renewal before the first lease expires, and checks native firing,
clear and expiry behavior.
