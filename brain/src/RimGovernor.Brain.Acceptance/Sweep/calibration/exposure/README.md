# Raid exposure calibration data

The per-colonist hit rows behind `Policy/Harm/exposure.json` (H5 of the harm model, #3543, epic #3471). One JSON object
per line, one line per seed and staged colonist (`ExposureRow` in `SweepExposure.cs`): `fixture`, `seed`, `ratio` (raid
points over defense capacity), `role`, `hits`, `downed`, `dead`. The method and the table format are in
[harm exposure calibration](../../../../../../docs/developers/testing/harm-exposure-calibration.md).

| File | Purpose | Seeds |
| --- | --- | --- |
| `rows-training.jsonl` | Historical fitting partition, 2,040 rows | 1..20; also 21..40 for the four refitted civilian fixtures |
| `rows-validation.jsonl` | Historical checking partition, 1,480 rows | 21..40 for unchanged fixtures; 41..60 for refitted fixtures |

The current schema-2 table uses all 3,520 rows from both files. They remain disjoint at fixture/seed fight level.
The historical names preserve provenance, not a current untouched holdout claim. Estimates are explicitly empirical
and stability-unconfirmed; see the current table below. No failed comparison was relabeled as a pass.

## How it was recorded

Three production batches, each appended to both files, followed by the civilian evidence described below.
Sweep 2 of each batch adds `-seeds 21,22,...,40`; both sweeps of a batch ran
with `-workers 1`, one after the other.

| Item | Batch 1 (#3543) | Batch 2 (#3561) | Batch 3 (#3880) |
| --- | --- | --- | --- |
| Fixtures | lab-ranged, lab-open, lab-choke, lab-split-ranged, lab-base | lab-ranged-light, lab-ranged-lone, lab-ranged-overrun | lab-ranged-few, lab-melee-lone |
| Command | `acceptance sweep <fixtures> -root <bridge root> -output <fresh dir> -workers 1 -exposure <rows>` | the same; sweep 1 also `-record` (the fixtures' schema 2 baselines) | the same as batch 2 |
| Revision | 349350457 (main after #3552, schema 2 baselines) | bafa9a686 (main after #3551) plus the three fixtures | 3936d269e (main after #3923) plus the two fixtures |
| Game `Assembly-CSharp.dll` SHA256 | EB0826B85AF430D13962E5C097EF53D869CF6A031656FB6A3673168A761C9F23 | 4B149EBE693BED7CD63535F69CCD5EC28D1622C43BDEAE8855B158D4B958138D | 0BE7A7EA4B3111D4430F45B36C234A5E43287880B0514B23915F2EF9A0629E16 |
| Box | Windows 11 dev box; 2 (sweep 1) and 3 (sweep 2) RimWorld processes before dispatch | the same box; 1 (sweep 1) and 0 (sweep 2) RimWorld processes before dispatch | the same box; 0 RimWorld processes before dispatch in both sweeps |
| Sweep 1 wall | 158 s lab-ranged, 95 s lab-open, 109 s lab-choke, 452 s lab-split-ranged, 212 s lab-base | 373 s lab-ranged-light, 119 s lab-ranged-lone, 244 s lab-ranged-overrun | 256 s lab-ranged-few, 170 s lab-melee-lone |
| Outcome | 20 of 20 seeds scored per fixture in both sweeps (440 rows per sweep) | 20 of 20 seeds scored per fixture in both sweeps (200 rows per sweep) | 20 of 20 seeds scored and resolved per fixture in both sweeps (140 rows per sweep) |

Staging digests (identical in both sweeps): lab-ranged 63087ab0, lab-open 7326005b, lab-choke 01254d34, lab-split-ranged
785e79c5, lab-base 719969ee, lab-ranged-light 62d4e4a0, lab-ranged-lone e8641761, lab-ranged-overrun fcdbc933,
lab-ranged-few f3bd7014, lab-melee-lone 3cb68157.

Each cell's rows come from one batch, so one game build, except `0-0.5` / gunner: lab-ranged-lone (batch 2) and
lab-ranged-few (batch 3). A one-seed re-run of lab-ranged-lone on the batch 3 build gave 0 hits in 4 trials against seed
1's committed 1 in 4.

## What the fixtures reach

A fixture stages one raid, so its ratio is one number for all of its seeds:

| Fixture | Ratio | Bin | Roles |
| --- | --- | --- | --- |
| lab-melee-lone | 0.30 | 0-0.5 | melee |
| lab-ranged-lone | 0.30 | 0-0.5 | gunner |
| lab-ranged-few | 0.40 | 0-0.5 | gunner |
| lab-ranged-light | 0.90 | 0.5-1 | gunner |
| lab-ranged | 1.20 | 1-2 | gunner |
| lab-open | 1.98 | 1-2 | gunner |
| lab-split-ranged | 2.41 | 2-4 | gunner |
| lab-base | 2.48 | 2-4 | gunner, melee |
| lab-choke | 3.67 | 2-4 | gunner, melee |
| lab-ranged-overrun | 4.21 | 4+ | gunner |

The three batch 2 fixtures are lab-ranged's sandbag line with the counts changed: four riflemen against three rifle
raiders (light), four against one (lone), two against seven (overrun). Batch 3's lab-ranged-few is the same line with three
riflemen against one; lab-melee-lone is four steel longsword fighters holding a line in the open against one club
drifter (Drifter, combatPower 35), the lone attacker a creepjoiner turning or a held entity escaping is.

## Historical seven-cell table and stability

The range is sweep 1's 90% bootstrap range of mean hits per colonist per raid; the check is that sweep 2's mean falls inside it.

| Cell | Trials (sweep 1 / 2) | Mean hits | 90% range | Sweep 2 mean | Stable | Down rate (95% Wilson) |
| --- | --- | --- | --- | --- | --- | --- |
| 0-0.5 / gunner | 140 / 140 | 0.1857 | 0.1214 to 0.2500 | 0.1857 | yes | 0.0000 (0.0000 to 0.0267) |
| 0-0.5 / melee | 80 / 80 | 0.3375 | 0.2250 to 0.4625 | 0.3750 | yes | 0.0000 (0.0000 to 0.0458) |
| 0.5-1 / gunner | 80 / 80 | 1.2500 | 0.9375 to 1.5500 | 1.2875 | yes | 0.1500 (0.0879 to 0.2441) |
| 1-2 / gunner | 140 / 140 | 2.6143 | 2.3000 to 2.9429 | 2.8000 | yes | 0.3143 (0.2432 to 0.3953) |
| 2-4 / gunner | 200 / 200 | 3.3400 | 3.0500 to 3.6450 | 3.3450 | yes | 0.5750 (0.5057 to 0.6415) |
| 2-4 / melee | 100 / 100 | 2.7800 | 2.3400 to 3.2100 | 2.7400 | yes | 0.3200 (0.2367 to 0.4166) |
| 4+ / gunner | 40 / 40 | 5.4000 | 5.1000 to 5.7250 | 5.3750 | yes | 0.9750 (0.8712 to 0.9956) |

Cells that failed the old pooled comparison: none. Batch 2's lab-ranged-lone alone failed `0-0.5` / gunner (sweep 1 0.1625 with range
0.0875 to 0.25, sweep 2 0.075: 13 hits in 80 trials, then 6); with batch 3's lab-ranged-few (sweep 1 0.217, sweep 2 0.333
over 60 trials) the pooled cell held. This historical table had no civilian rows and is superseded by the empirical table below.

Runs are not bit-reproducible (the planner is served on wall-clock cadence), so a re-run yields close, not identical, rows.

## Civilian evidence (#3969)

The row files also contain the five civilian fixtures' approved 200-fight calibration: seeds 1..20 for fitting,
then 21..40 for independent validation, with one worker and no other game runs. Each batch contributes 700 rows.
Every seed had elapsed ticks, combat contact, planner orders and the expected four gunners, one unarmed pawn,
one pawn natively incapable of violence, and one initially downed pawn. All five one-seed diagnostics passed
before these batches; diagnostic rows are excluded. The native game retained normal shelter and medical care.

The original civilian candidate was not promoted: five cells failed the existing stability rule.
The seven-cell table above came from batches 1–3. This original candidate and the refitted candidate below failed
the old rule; the approved empirical estimate uses their observations with explicit evidence qualifications.

The checkout was `0b3c895ef`; the harness build was `7bfece8c9` (identical harness source), with native build
`3f8795381` (identical native source). The harness ends resolved fights early using native outcome reads and
retains the existing finite tick limit. Game `Assembly-CSharp.dll` SHA256:
`0BE7A7EA4B3111D4430F45B36C234A5E43287880B0514B23915F2EF9A0629E16`.

Both commands used `acceptance sweep <five fixtures> -root <private bridge> -output <fresh directory>
-workers 1 -exposure <fresh rows>`. The first also used `-record`; the second used explicit seeds 21..40.

| Fixture | Native ratio | Staging digest | First / second batch wall (seconds) |
| --- | --- | --- | --- |
| lab-civilians-small | 0.285357 | d9670ac8 | 135.338 / 177.175 |
| lab-civilians-light | 0.856071 | 9a14fff1 | 339.118 / 307.129 |
| lab-civilians-even | 1.712142 | 0d1a8552 | 647.879 / 652.062 |
| lab-civilians-heavy | 3.424285 | 98f3a90d | 371.798 / 398.850 |
| lab-civilians-overrun | 6.848570 | 60952ac1 | 244.600 / 230.836 |

All 200 seeds scored. First-batch light seed 13 reached the unchanged limit at 7,503 ticks with the surviving
standing colonist retreating and two hostiles pursuing; it had contact and 21 issued orders, no run error, and
contributes its bounded exposure under `SweepExposure.Rows`. The other 199 fights resolved. Neither an
unresolved outcome nor a zero-hit pawn was discarded.

The original candidate passed 17 of 22 populated cells. All unarmed cells passed; the following cells failed. Each civilian
cell has 20 trials in each batch. These are results requiring additional evidence, not replacement zero values
or proxy roles. Any further full sweep needs approval under `AGENTS.md`.

| Cell | First mean | First 90% range | Second mean |
| --- | --- | --- | --- |
| 0-0.5 / noncombatant | 0.45 | 0.20 to 0.70 | 0.75 |
| 0-0.5 / downed | 0.00 | 0.00 to 0.00 | 0.05 |
| 0.5-1 / noncombatant | 3.25 | 2.50 to 3.95 | 2.00 |
| 2-4 / noncombatant | 4.95 | 4.45 to 5.40 | 4.35 |
| 4+ / noncombatant | 5.50 | 5.15 to 5.90 | 6.25 |

### Fresh checks after refitting

The user approved up to 100 additional fights. The four originally failing fixtures used their first 40 seeds for
refitting, then ran fresh seeds 41..60, one worker and no competing game: 80 fights, 560 rows, all scored and resolved.
The remaining 20 approved fights were not spent. Even retained its original split. Staging digests, native ratios,
game DLL and installed native/harness sources were unchanged. Wall times were 135.709 s (small), 206.122 s (light),
331.326 s (heavy), and 213.527 s (overrun).

Before checking these fresh rows, the fitting raw rows were frozen at SHA256
`05F3F715B467181358CB5682C2AF8BA806C35C9045B0415214E89D584F1911D5`; their old-method table at
`A18316BC6B3C236BF1568AE48FAA3D3998041335EDAB683B5408C295DE1A9DB7`.
The old comparison passed 15 of 22 cells. Its seven failures are retained here:

| Cell | Refitted mean | Refitted 90% range | Checking mean |
| --- | --- | --- | --- |
| 0-0.5 / noncombatant | 0.6000 | 0.4000 to 0.8250 | 0.2000 |
| 0.5-1 / gunner | 1.4417 | 1.2542 to 1.6417 | 1.2312 |
| 0.5-1 / downed | 0.0000 | 0.0000 to 0.0000 | 0.0500 |
| 2-4 / gunner | 4.1028 | 3.8917 to 4.3028 | 3.8357 |
| 2-4 / unarmed | 5.2000 | 4.9000 to 5.5250 | 4.7500 |
| 4+ / noncombatant | 5.8750 | 5.4750 to 6.2750 | 5.0000 |
| 4+ / unarmed | 4.9750 | 4.7500 to 5.2250 | 5.5000 |

The comparison used an interval for the fitting mean alone, omitting uncertainty in the checking mean. All-zero
fitting rows necessarily yielded a zero bootstrap interval. Selective refitting also changed pooled fixture weights:
for light gunners the fit was one-third legacy and two-thirds civilian, while the check was half of each.
The small noncombatant discrepancy remained material even after allowing uncertainty in both means.

The user explicitly approved qualified empirical estimates instead of this veto. All rounds are now estimation
data. No new predictive validation success is claimed. The builder fixes each fixture's mixture weight at its average
role-pawn count per raid and resamples whole raids within each fixture. This preserves correlation and prevents extra
sampling rounds from increasing a fixture's mixture weight. Sparse or zero samples retain their actual observations,
without positive floors or invented immunity. Ranges can understate unseen risk and forecasts can underprice or overprice it.

## Current empirical table

Every reading reports `exposure_empirical_estimate`, `exposure_stability_unconfirmed` and
`exposure_layout_unassessed`. Fewer than 20 hit raids adds a cell-specific sparse finding; no observed hit raids adds
the zero-hit finding. The minimum evidence threshold is 20 distinct raids. Hits use the 90% and downs the 95%
raid-clustered empirical percentile ranges; neither certifies accuracy or bounds unobserved events.

| Cell | Pawn rows / raids / hit raids | Mean hits | 90% empirical range | Down mean (95% empirical range) |
| --- | --- | --- | --- | --- |
| 0-0.5 / gunner | 520 / 140 / 66 | 0.1924 | 0.1576 to 0.2311 | 0.0068 (0.0015 to 0.0144) |
| 0-0.5 / melee | 160 / 40 / 32 | 0.3563 | 0.2875 to 0.4188 | 0 (0 to 0) |
| 0-0.5 / noncombatant | 60 / 60 / 19 | 0.4667 | 0.3 to 0.6333 | 0 (0 to 0) |
| 0-0.5 / unarmed | 60 / 60 / 0 | 0 | 0 to 0 | 0 (0 to 0) |
| 0-0.5 / downed | 60 / 60 / 1 | 0.0167 | 0 to 0.05 | 1 (1 to 1) |
| 0.5-1 / gunner | 400 / 100 / 99 | 1.3427 | 1.1615 to 1.5302 | 0.15 (0.1021 to 0.2021) |
| 0.5-1 / noncombatant | 60 / 60 / 49 | 2.7333 | 2.3333 to 3.1333 | 0.4 (0.2833 to 0.5167) |
| 0.5-1 / unarmed | 60 / 60 / 6 | 0.5333 | 0.2 to 0.9167 | 0.0833 (0.0167 to 0.1667) |
| 0.5-1 / downed | 60 / 60 / 1 | 0.0167 | 0 to 0.05 | 1 (1 to 1) |
| 1-2 / gunner | 440 / 120 / 117 | 3.5318 | 3.3295 to 3.7455 | 0.5614 (0.5091 to 0.6182) |
| 1-2 / noncombatant | 40 / 40 / 40 | 4.925 | 4.725 to 5.125 | 1 (1 to 1) |
| 1-2 / unarmed | 40 / 40 / 40 | 4.875 | 4.65 to 5.1 | 1 (1 to 1) |
| 1-2 / downed | 40 / 40 / 2 | 0.05 | 0 to 0.1 | 1 (1 to 1) |
| 2-4 / gunner | 640 / 180 / 139 | 3.8327 | 3.6661 to 3.9964 | 0.7054 (0.6679 to 0.7429) |
| 2-4 / melee | 200 / 80 / 80 | 2.76 | 2.59 to 2.92 | 0.315 (0.27 to 0.355) |
| 2-4 / noncombatant | 60 / 60 / 60 | 4.75 | 4.45 to 5.0667 | 1 (1 to 1) |
| 2-4 / unarmed | 60 / 60 / 60 | 5.05 | 4.8 to 5.3 | 1 (1 to 1) |
| 2-4 / downed | 60 / 60 / 11 | 0.2167 | 0.1167 to 0.3167 | 1 (1 to 1) |
| 4+ / gunner | 320 / 100 / 100 | 5.4069 | 5.2819 to 5.5306 | 0.9917 (0.9792 to 1) |
| 4+ / noncombatant | 60 / 60 / 60 | 5.5833 | 5.2833 to 5.9167 | 1 (1 to 1) |
| 4+ / unarmed | 60 / 60 / 60 | 5.15 | 4.9333 to 5.3667 | 1 (1 to 1) |
| 4+ / downed | 60 / 60 / 6 | 0.1 | 0.0333 to 0.1667 | 1 (1 to 1) |
