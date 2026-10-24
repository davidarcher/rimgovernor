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

## The capacity's ranged hit chance

Defense capacity counts each colonist's ranged DPS at its hit factor (#3845): the pawn's game hit chance with its
primary over the reference gunner's (`PawnEquipment.RangedHitFactor`, read by `NativeDefenseStats.PawnRangedHitFactor`).

- The hit chance is `ShotReport`'s standard-target aim chance: `HitFactorFromShooter(pawn, d)` (1 for a verb that
  cannot go wild) times the weapon's `VerbProperties.GetHitChanceFactor(weapon, d)`, floored at
  `ShootTuning.MinAimOnChance_StandardTarget`. Weather, blind smoke, darkness and cover are situational and stay
  neutral; the target is a standing human-sized one.
- The distance `d` is where the game's AI shoots from: `CastPositionFinder` scores a cell by its distance to the target
  against `OptimalRangeFactor` (0.8) times the verb's range. It uses the verb's own range, without the weather cap.
- The reference gunner is the shooter both anchors of the scale were measured with: `DefensePointsPerDps` (a pirate
  gunner with an assault rifle) and this calibration, whose every gunner is a lab colonist at Shooting 8 with no traits
  and a normal assault rifle. Its accuracy is `ShootingAccuracyPawn` at the game's no-skill offset (`noSkillOffset` 8,
  the same level), and its rifle's accuracy is the def's abstract stats at the rifle's own engagement distance (24.72
  cells). Its hit chance is about 0.237.

The exposure table needs no recalibration. A calibration gunner is the reference gunner, so its factor is exactly 1;
melee and turrets are unchanged. So each fixture's capacity, its ratio and the bin of every committed row are the same
under the hit-weighted capacity, and `exposure.json` holds as it is. The staging reply's `RangedDps` carries the hit
factor too, so a new sweep measures the capacity the colony reads.

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

## Calibration result (H5)

`exposure.json` is built from sweep 1 (seeds 1..20) of the ten plain-raid fixtures below; sweep 2 (seeds 21..40)
checked it. The rows were recorded in three batches (#3543, #3561, #3880). The rows, commands, revisions, game hashes,
per-cell trials and ranges, and the stability result are in
[`Sweep/calibration/exposure/README.md`](../../../brain/src/RimGovernor.Brain.Acceptance/Sweep/calibration/exposure/README.md).

| Cell | Trials | Mean hits | 90% range |
| --- | --- | --- | --- |
| 0-0.5 / gunner | 140 | 0.19 | 0.12 to 0.25 |
| 0-0.5 / melee | 80 | 0.34 | 0.23 to 0.46 |
| 0.5-1 / gunner | 80 | 1.25 | 0.94 to 1.55 |
| 1-2 / gunner | 140 | 2.61 | 2.30 to 2.94 |
| 2-4 / gunner | 200 | 3.34 | 3.05 to 3.65 |
| 2-4 / melee | 100 | 2.78 | 2.34 to 3.21 |
| 4+ / gunner | 40 | 5.40 | 5.10 to 5.72 |

These seven cells are stable (sweep 2's mean is inside sweep 1's range). A fixture stages one raid, so it reaches one
ratio: lab-melee-lone 0.30, lab-ranged-lone 0.30, lab-ranged-few 0.40, lab-ranged-light 0.90, lab-ranged 1.20, lab-open
1.98, lab-split-ranged 2.41, lab-base 2.48, lab-choke 3.67, lab-ranged-overrun 4.21. The `0-0.5` bin prices the small
fights a colony has besides raids: a creepjoiner turning aggressive (its kind's `combatPower`, 55 in the game) and a held
entity escaping (#3880). lab-ranged-lone alone failed stability there (hits are rare at its ratio); with lab-ranged-few
the gunner cell holds. Uncalibrated: the `noncombatant` role in every bin (#3969), and every bin for roles other than the
cells above. Cells are not extrapolated across bins.

### Re-running the calibration

Run both sweeps one after the other with `-workers 1` (about 30 minutes each on the dev box), detached, into fresh output
directories and fresh rows files:

```
RimGovernor.cmd acceptance sweep lab-ranged lab-open lab-choke lab-split-ranged lab-base lab-ranged-light lab-ranged-lone lab-ranged-overrun lab-ranged-few lab-melee-lone -root <bridge root> -output <fresh dir> -workers 1 -exposure <rows1.jsonl>
RimGovernor.cmd acceptance sweep lab-ranged lab-open lab-choke lab-split-ranged lab-base lab-ranged-light lab-ranged-lone lab-ranged-overrun lab-ranged-few lab-melee-lone -root <bridge root> -output <fresh dir> -workers 1 -seeds 21,22,...,40 -exposure <rows2.jsonl>
RimGovernor.cmd acceptance sweep -exposure-table <rows1.jsonl> Policy/Harm/exposure.json
```

Build a table from the second rows file as well and check that each cell's mean lies inside the first table's
`hitsLo`..`hitsHi`. A cell that fails stays uncalibrated in the committed table (remove its cell, or set trials to 0), and
the README lists it. Commit the rows, the table and the README together. To add fixtures without re-running the others,
sweep only the new ones, append their rows to both committed rows files and rebuild the table from the first.
