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

Roles use the native staged read, in priority order: `downed`; `noncombatant` when incapable of violence;
`unarmed` when standing and violence-capable without a primary; `gunner` with a ranged primary; `melee` with a melee
primary. This is the same definition the live forecast uses. Capacity counts only standing violence-capable colonists.
A staged turret counts only with a power trader that is on, as in the colony read. A planned retreat is not immunity.

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
share one file. A failed seed, or one with no elapsed game ticks, combat contact or issued planner orders,
contributes no exposure rows. A served fight still includes colonists who received zero hits.
A real-game sweep costs minutes per seed: run one seed to check a change, and the full 20 seeds only
for a calibration (H5).

## Table

```
RimGovernor.cmd acceptance sweep -exposure-table <rows.jsonl> <exposure.json>
```

builds `Policy/Harm/exposure.json` (schema 2), an embedded resource of `RimGovernor.Brain.Policy`. A cell is a ratio bin by role. Bins
include their lower edge and exclude the upper: `0-0.5`, `0.5-1`, `1-2`, `2-4` and `4+` (edges 0.5, 1, 2, 4). A trial is
one colonist in one seed's raid. A cell holds:

- `trials`: all recorded pawn rows; `raids`: distinct fixture/seed fights; `hitRaids`: fights with at least one hit on a pawn of this role;
- `meanHits` per colonist per raid with a 90% empirical percentile-bootstrap range (`hitsLo`, `hitsHi`);
- `downRate`, the share that ended down or dead, with a 95% empirical percentile-bootstrap range (`downLo`, `downHi`).

Each fixture has a fixed mixture weight equal to its average number of this role's pawns per raid. Its mean uses all
its recorded pawn rows. Weighted fixture means therefore represent equally sized raid cohorts, even when one fixture
has more sampling rounds. The 2,000 deterministic bootstrap draws resample whole seed raids within each fixture and
retain those weights. Four gunners in one raid remain correlated observations. Fixture and seed ordering is canonical.

These are descriptive empirical estimates. The ranges do not certify predictive accuracy, future-batch coverage or
stability. In particular, resampling all-zero observations produces a zero interval, which is not a bound on unseen
hits or proof of immunity. No fabricated hits, positive floors or substitute roles are added.

The step then reads the file back through `RaidExposure` and prints what each cell answers.

## Reader

`RaidExposure.Load()` parses the embedded table once. `RaidExposure.HitsPerColonist(ratio, role)` returns a
`ExposureReading.Calibrated` carrying a `HitRange` (mean, low, high) and findings, so the ledger reads a range and never only a mean.
`ExposureReading.Uncalibrated` names `exposure_cell_uncalibrated:<bin>/<role>` for a cell under 20 distinct raids, an absent cell, or a ratio outside the
table (`outside`). `RaidExposure.DownRate` answers the down rate the same way. Every reading carries
`exposure_layout_unassessed`: the defense layout is a reported key, not a table axis. All table-backed readings also
carry `exposure_empirical_estimate` and `exposure_stability_unconfirmed`. A zero-hit sample adds
`exposure_zero_hit_sample:<bin>/<role>:0/<raids>`; otherwise fewer than 20 hit raids adds
`exposure_sparse_hit_sample:<bin>/<role>:<hitRaids>/<raids>`. Both hits and down-rate readings retain these qualifications.
Only a dead pawn has the separate exact no-future-hits reason. A living sheltered or downed pawn needs its own evidence.

## Calibration result (H5)

`exposure.json` uses every recorded row from both historical evidence files, including the ten original plain-raid
fixtures and the five civilian fixtures. There are 22 cells: gunner in every bin, melee in `0-0.5` and `2-4`, and
unarmed, noncombatant and initially downed in every bin. Other melee bins remain unavailable, without extrapolation.
The rows, commands, revisions, game hashes, current per-cell values and historical failed stability results are in
[`Sweep/calibration/exposure/README.md`](../../../brain/src/RimGovernor.Brain.Acceptance/Sweep/calibration/exposure/README.md).

Each fixture stages one raid ratio: lab-melee-lone 0.30, lab-ranged-lone 0.30, lab-ranged-few 0.40, lab-ranged-light
0.90, lab-ranged 1.20, lab-open 1.98, lab-split-ranged 2.41, lab-base 2.48, lab-choke 3.67,
lab-ranged-overrun 4.21. The `0-0.5` bin prices the small
fights a colony has besides raids: a creepjoiner turning aggressive (its kind's `combatPower`, 55 in the game) and a held
entity escaping (#3880). The fixture mixture remains a modeling approximation; layouts are not an exposure axis.

### Civilian calibration fixtures

The original seven cells contained no unarmed, incapable or initially downed trial. In particular,
`lab-melee-lone` stages steel longswords, so its melee cell cannot answer for fists.
The five `lab-civilians-*` fixtures stage four reference riflemen, one unarmed violence-capable colonist,
one colonist whose native childhood disables violence, and one anesthetized colonist. The planner retains
its normal hold, blocker, shelter and rescue behavior. Native readback checks each requested state and
refuses a fixture whose measured points/capacity ratio misses its intended bin.

| Fixture | Hostile riflemen | Intended bin | New trial roles per seed |
| --- | --- | --- | --- |
| lab-civilians-small | 1 | 0-0.5 | unarmed, noncombatant, downed |
| lab-civilians-light | 3 | 0.5-1 | unarmed, noncombatant, downed |
| lab-civilians-even | 6 | 1-2 | unarmed, noncombatant, downed |
| lab-civilians-heavy | 12 | 2-4 | unarmed, noncombatant, downed |
| lab-civilians-overrun | 24 | 4+ | unarmed, noncombatant, downed |

The approved calibration recorded 20 fitting seeds and 20 independent validation seeds per fixture after
all five one-seed diagnostics passed. Four fixtures then received 20 fresh seeds each: 280 scored civilian fights,
1,960 pawn rows. Both failed validation rounds are retained in the evidence README. All recorded rounds now contribute
to the qualified empirical estimate; none is presented as an untouched confirmatory holdout.

The former rule required the validation mean to fall inside the training mean's 90% bootstrap interval. That interval
omitted sampling uncertainty in the validation mean, became narrower on refitting, and degenerated to zero for all-zero
samples. Selectively adding training rounds also changed pooled fixture weights. Following review and explicit user
approval, failed comparisons qualify the estimates rather than veto their use. Predictions can underprice or overprice risk.

A full calibration requires explicit approval. These batches took about 28–30 minutes each, excluding
the native rebuild (20+ minutes); the additional 80 fights took about 15 minutes. Additional full sweeps need approval.

### Re-running the calibration

Run both sweeps one after the other with `-workers 1` (about 30 minutes each on the dev box), detached, into fresh output
directories and fresh rows files:

```
RimGovernor.cmd acceptance sweep lab-ranged lab-open lab-choke lab-split-ranged lab-base lab-ranged-light lab-ranged-lone lab-ranged-overrun lab-ranged-few lab-melee-lone -root <bridge root> -output <fresh dir> -workers 1 -exposure <rows1.jsonl>
RimGovernor.cmd acceptance sweep lab-ranged lab-open lab-choke lab-split-ranged lab-base lab-ranged-light lab-ranged-lone lab-ranged-overrun lab-ranged-few lab-melee-lone -root <bridge root> -output <fresh dir> -workers 1 -seeds 21,22,...,40 -exposure <rows2.jsonl>
RimGovernor.cmd acceptance sweep -exposure-table <rows1.jsonl> Policy/Harm/exposure.json
```

The commands above illustrate recording separate rounds; do not replace production with the first round alone.
For the committed evidence, concatenate `rows-training.jsonl` and `rows-validation.jsonl` into an ignored temporary
rows file, then pass that file to `-exposure-table`. The file names retain the historical split, not a current
train/holdout claim. `SweepFileShapeTests` rebuilds from their union and checks exact equality with the embedded table
and disjoint fixture/seed fights across the files. Keep every pawn in its recorded fight, including zero-hit pawns.
Commit the raw rows, generated table and evidence README together. A new claim of confirmed predictive accuracy needs
a frozen validation protocol, a meaningful error criterion and fresh evidence; these descriptive ranges do not supply it.
