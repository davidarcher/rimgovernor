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

| Item | Value |
| --- | --- |
| Command | `acceptance sweep lab-ranged lab-open lab-choke lab-split-ranged lab-base -root <bridge root> -output <fresh dir> -workers 1 -exposure <rows>`; sweep 2 adds `-seeds 21,22,...,40` |
| Revision | 349350457 (main after #3552, schema 2 baselines) |
| Game | pinned `Assembly-CSharp.dll`, SHA256 EB0826B85AF430D13962E5C097EF53D869CF6A031656FB6A3673168A761C9F23 |
| Workers | 1 for both sweeps, run one after the other |
| Box | Windows 11 dev box; another session's RimWorld process was running (2 RimWorld processes before dispatch in sweep 1, 3 in sweep 2); sweep 1 wall 158 s lab-ranged, 95 s lab-open, 109 s lab-choke, 452 s lab-split-ranged, 212 s lab-base |
| Outcome | 20 of 20 seeds scored for every fixture in both sweeps (440 colonist rows per sweep) |

Staging digests (identical in both sweeps): lab-ranged 63087ab0, lab-open 7326005b, lab-choke 01254d34, lab-split-ranged
785e79c5, lab-base 719969ee.

## What the fixtures reach

A fixture stages one raid, so its ratio is one number for all of its seeds:

| Fixture | Ratio | Bin | Roles |
| --- | --- | --- | --- |
| lab-ranged | 1.20 | 1-2 | gunner |
| lab-open | 1.98 | 1-2 | gunner |
| lab-split-ranged | 2.41 | 2-4 | gunner |
| lab-base | 2.48 | 2-4 | gunner, melee |
| lab-choke | 3.67 | 2-4 | gunner, melee |

No fixture reaches the `0-0.5`, `0.5-1` or `4+` bins, so those cells have no rows and stay uncalibrated. Uncalibrated: the 0-0.5, 0.5-1 and 4+ bins (every role), and every 1-2 and 2-4 cell for roles other than the three
calibrated ones. The fixtures' ratios are 1.20 to 3.67, so the issue's claim that they cover 0.5-1 was wrong. New fixtures
for those bins are tracked in #3561.

## Table (sweep 1) and stability (sweep 2)

The range is sweep 1's 90% bootstrap range of mean hits per colonist per raid; the check is that sweep 2's mean falls inside it.

| Cell | Trials (sweep 1 / 2) | Mean hits | 90% range | Sweep 2 mean | Stable | Down rate (95% Wilson) |
| --- | --- | --- | --- | --- | --- | --- |
| 1-2 / gunner | 140 / 140 | 2.6143 | 2.3000 to 2.9429 | 2.8000 | yes | 0.3143 (0.2432 to 0.3953) |
| 2-4 / gunner | 200 / 200 | 3.3400 | 3.0500 to 3.6450 | 3.3450 | yes | 0.5750 (0.5057 to 0.6415) |
| 2-4 / melee | 100 / 100 | 2.7800 | 2.3400 to 3.2100 | 2.7400 | yes | 0.3200 (0.2367 to 0.4166) |

Cells that failed stability: none. Cells with no rows (uncalibrated, absent from the table): everything else, including
`0.5-1` / gunner, which this issue asked for and no existing fixture can reach (see the calibration doc).

Runs are not bit-reproducible (the planner is served on wall-clock cadence), so a re-run yields close, not identical, rows.
