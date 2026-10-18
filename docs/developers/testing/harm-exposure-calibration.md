# Raid exposure calibration

Step 3 of the [harm model](../architecture/forecast-value.md#harm-model) needs the hits a colonist takes per raid.
They cannot be derived in closed form, so the real-game `acceptance sweep` calibrates them and the policy reads a
committed table. No combat simulator is rebuilt.

## What a sweep records

Each seed's metrics (`CombatMetrics.Metrics`, schema 2) carry, beside the fight totals:

- `colonists`: one row per staged colonist with `hits` (damage ledger rows where the colonist is the victim and the
  instigator is hostile; friendly fire and hits on hostiles are not counted), `role`, `downed` (ended down and alive)
  and `dead`.
- `raidPoints`: the sum of the staged hostiles' pawn-kind `combatPower`.
- `defenseCapacity`: `DefenseCapacityPolicy.DefenseCapacity` over the staged colonists and turrets, the same function
  and units the colony reads live.

The staging supplies these. `test/stage` replies, for test use only, each pawn's `CombatPower`, `MeleeDps`,
`RangedDps` and `Primary` (none, melee, ranged) and each building group's player turret `Guns` (`Dps`, `Powered`),
measured with `NativeDefenseStats` as the colony read measures them. The sweep stamps `staged`, `roles`, `raidPoints`
and `defenseCapacity` on the first read of the bundle (`combat_reads.jsonl`), so `CombatLabBundle.AggregateBundleAsync`
recomputes the same metrics from a committed bundle without a game. A bundle recorded before schema 2 has no stamp and
gets no colonist rows. Fixtures declare nothing for this.

Roles: `gunner` with a ranged primary; `melee` with a melee primary, or unarmed (the defense planner drafts every lab
colonist that can fight); `noncombatant` when staged downed. Every staged colonist counts as able to fight in the
capacity. A staged turret counts only with a power trader that is on, as in the colony read.

## Rows

```
RimGovernor.cmd acceptance sweep lab-open -root <dir> -output <dir> -seeds 1 -exposure <rows.jsonl>
```

appends one JSON line per seed and colonist: `fixture`, `seed`, `ratio` (raid points over defense capacity; null with no
capacity, which falls in the top bin), `role`, `hits`, `downed`, `dead`. Appending lets several fixtures and sweeps
share one file. A real-game sweep costs minutes per seed: run one seed to check a change, and the full 20 seeds only
for a calibration (H5).

## Table

```
RimGovernor.cmd acceptance sweep -exposure-table <rows.jsonl> <exposure.json>
```

builds `Policy/Harm/exposure.json`, an embedded resource of `RimGovernor.Brain.Policy`. A cell is a ratio bin by role. Bins
include their lower edge and exclude the upper: `0-0.5`, `0.5-1`, `1-2`, `2-4` and `4+` (edges 0.5, 1, 2, 4). A trial is
one colonist in one seed's raid. A cell holds:

- `trials`;
- `meanHits` per colonist per raid with a 90% percentile-bootstrap range (`hitsLo`, `hitsHi`; 2000 resamples, seeded, so
  the same rows give the same table);
- `downRate`, the share that ended down or dead, with its 95% Wilson interval (`downLo`, `downHi`), the interval
  `HuntingQualification` uses.

The step then reads the file back through `RaidExposure` and prints what each cell answers.

## Reader

`RaidExposure.Load()` parses the embedded table once. `RaidExposure.HitsPerColonist(ratio, role)` returns a
`Fact<HitRange>` (mean, low, high) with findings, so the ledger reads a range and never only a mean. The answer is
unknown with `exposure_cell_uncalibrated:<bin>/<role>` for a cell under 20 trials, an absent cell, or a ratio outside the
table (`outside`). `RaidExposure.DownRate` answers the down rate the same way. Every reading carries
`exposure_layout_unassessed`: the defense layout is a reported key, not a table axis, in v1.

`exposure.json` is a fixture table until the calibration run (H5) commits the calibrated one; its single cell has no trials,
so every reading is uncalibrated. Committed baselines in `Sweep/baselines` are schema 1 and re-record in H5.
