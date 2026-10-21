# Raid exposure calibration data

The per-colonist hit rows behind `Policy/Harm/exposure.json` (H5 of the harm model, #3543, epic #3471). One JSON object
per line, one line per seed and staged colonist (`ExposureRow` in `SweepExposure.cs`): `fixture`, `seed`, `ratio` (raid
points over defense capacity), `role`, `hits`, `downed`, `dead`. The method and the table format are in
[harm exposure calibration](../../../../../../docs/developers/testing/harm-exposure-calibration.md).

| File | Sweep | Seeds |
| --- | --- | --- |
| `rows-seeds-1-20.jsonl` | 1 (builds the table) | 1..20 |
| `rows-seeds-21-40.jsonl` | 2 (stability check) | 21..40 |

## How it was recorded

Two batches, each appended to both files. Sweep 2 of each batch adds `-seeds 21,22,...,40`; both sweeps of a batch ran
with `-workers 1`, one after the other.

| Item | Batch 1 (#3543) | Batch 2 (#3561) |
| --- | --- | --- |
| Fixtures | lab-ranged, lab-open, lab-choke, lab-split-ranged, lab-base | lab-ranged-light, lab-ranged-lone, lab-ranged-overrun |
| Command | `acceptance sweep <fixtures> -root <bridge root> -output <fresh dir> -workers 1 -exposure <rows>` | the same; sweep 1 also `-record` (the fixtures' schema 2 baselines) |
| Revision | 349350457 (main after #3552, schema 2 baselines) | bafa9a686 (main after #3551) plus the three fixtures |
| Game `Assembly-CSharp.dll` SHA256 | EB0826B85AF430D13962E5C097EF53D869CF6A031656FB6A3673168A761C9F23 | 4B149EBE693BED7CD63535F69CCD5EC28D1622C43BDEAE8855B158D4B958138D |
| Box | Windows 11 dev box; 2 (sweep 1) and 3 (sweep 2) RimWorld processes before dispatch | the same box; 1 (sweep 1) and 0 (sweep 2) RimWorld processes before dispatch |
| Sweep 1 wall | 158 s lab-ranged, 95 s lab-open, 109 s lab-choke, 452 s lab-split-ranged, 212 s lab-base | 373 s lab-ranged-light, 119 s lab-ranged-lone, 244 s lab-ranged-overrun |
| Outcome | 20 of 20 seeds scored per fixture in both sweeps (440 rows per sweep) | 20 of 20 seeds scored per fixture in both sweeps (200 rows per sweep) |

Staging digests (identical in both sweeps): lab-ranged 63087ab0, lab-open 7326005b, lab-choke 01254d34, lab-split-ranged
785e79c5, lab-base 719969ee, lab-ranged-light 62d4e4a0, lab-ranged-lone e8641761, lab-ranged-overrun fcdbc933.

Each cell's rows come from one batch, so each cell was measured on one game build.

## What the fixtures reach

A fixture stages one raid, so its ratio is one number for all of its seeds:

| Fixture | Ratio | Bin | Roles |
| --- | --- | --- | --- |
| lab-ranged-lone | 0.30 | 0-0.5 | gunner |
| lab-ranged-light | 0.90 | 0.5-1 | gunner |
| lab-ranged | 1.20 | 1-2 | gunner |
| lab-open | 1.98 | 1-2 | gunner |
| lab-split-ranged | 2.41 | 2-4 | gunner |
| lab-base | 2.48 | 2-4 | gunner, melee |
| lab-choke | 3.67 | 2-4 | gunner, melee |
| lab-ranged-overrun | 4.21 | 4+ | gunner |

The three batch 2 fixtures are lab-ranged's sandbag line with the counts changed: four riflemen against three rifle
raiders (light), four against one (lone), two against seven (overrun).

## Table (sweep 1) and stability (sweep 2)

The range is sweep 1's 90% bootstrap range of mean hits per colonist per raid; the check is that sweep 2's mean falls inside it.

| Cell | Trials (sweep 1 / 2) | Mean hits | 90% range | Sweep 2 mean | Stable | Down rate (95% Wilson) |
| --- | --- | --- | --- | --- | --- | --- |
| 0-0.5 / gunner | 80 / 80 | 0.1625 | 0.0875 to 0.2500 | 0.0750 | no | 0.0000 (0.0000 to 0.0458) |
| 0.5-1 / gunner | 80 / 80 | 1.2500 | 0.9375 to 1.5500 | 1.2875 | yes | 0.1500 (0.0879 to 0.2441) |
| 1-2 / gunner | 140 / 140 | 2.6143 | 2.3000 to 2.9429 | 2.8000 | yes | 0.3143 (0.2432 to 0.3953) |
| 2-4 / gunner | 200 / 200 | 3.3400 | 3.0500 to 3.6450 | 3.3450 | yes | 0.5750 (0.5057 to 0.6415) |
| 2-4 / melee | 100 / 100 | 2.7800 | 2.3400 to 3.2100 | 2.7400 | yes | 0.3200 (0.2367 to 0.4166) |
| 4+ / gunner | 40 / 40 | 5.4000 | 5.1000 to 5.7250 | 5.3750 | yes | 0.9750 (0.8712 to 0.9956) |

Cells that failed stability: `0-0.5` / gunner. Hits there are rare (13 in 80 trials in sweep 1, 6 in sweep 2), so the
bootstrap range is narrow against the seed-to-seed swing; the cell was removed from the committed table and reads
uncalibrated. Cells with no rows (uncalibrated, absent from the table): every other bin and role.

Runs are not bit-reproducible (the planner is served on wall-clock cadence), so a re-run yields close, not identical, rows.
