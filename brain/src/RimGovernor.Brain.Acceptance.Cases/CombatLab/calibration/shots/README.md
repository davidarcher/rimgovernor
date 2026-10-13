# Shot-level combat lab data

`lab-ranged.jsonl` is the real game's shot-level record of `lab-ranged`
(seeds 1..20, the baseline's seeds), recorded for calibrating `combatsim`
timing (#2917, epic #2830). One JSON object per line, in game order within a
seed, seeds ascending. Every row has `seed` and a kind `k`; schema and strict
parser are `shots.go` (`ParseShots`), pinned by `shots_test.go`.

## How it was recorded

The tap is `LabShotLedger` in `scripts/fixtures/DebugStartFixture.cs`
(Harmony postfixes/prefixes, observation only), installed beside the damage
ledger when the bridge discovers `test/lab_stage` (so runs resumed from a stage
bundle record too). The metrics case reads it with `test/lab_stage` action
`shots` after the run and writes `combat_shots.jsonl` next to
`combat_reads.jsonl`; `acceptance sweep lab-ranged -shots <file>` stamps each
seed's rows with the seed and joins impacts with the damage ledger
(`BuildShotDataset`).

| Item | Value |
| --- | --- |
| Command | `acceptance sweep lab-ranged -n 20 -workers 1 -shots <file>` |
| Revision | 4a1b8fe2b plus the #2917 change (fixture C#, `shots.go`, sweep `-shots`) |
| Game | pinned `Assembly-CSharp.dll`, `combatsim.AssemblyCSharpSHA256` 5CF1B5BE... |
| Workers | 1 (one game); 0 other RimWorld processes before dispatch |
| Box | Windows 11 dev box, no peer RimWorld process; CPU load sampled at 76% before the sweep (other processes); ~45 s wall per seed (909 s for 20 seeds) |
| Seeds | 1..20: each resumes the one staged checkpoint (stage digest 90aea800) and reseeds the game RNG (`-run-seed`) |
| Metrics of this sweep | 20 of 20 seeds scored, every row has first contact (99..134) and orders (9..13); colonist damage mean 53.65 (baseline 64.15); no colonist down or death |

Seed 14 was re-run alone: the first pass of the sweep re-staged the fixture
in-process (the case's staging code hash changed while the sweep ran), which
changes colonist mover timing (see below), so the committed seed-14 rows are
from a resumed run like the other 19.

Runs are not bit-reproducible: the planner is served on wall-clock cadence, so
two sweeps at the same seeds differ in hits and damage (the baseline file's own
rows differ from this sweep's). Opening ticks, burst cadence and the think
phase are the same in every seed.

## Rows

All ticks are game ticks (`TicksGame`). `pawn`, `shooter`, `target`, `hit` and
`thing` are `GetUniqueLoadID` values (stable across seeds: same checkpoint).
`side` is `colonist`, `hostile` or `other` (animals). Cells are map cells.

`k = "shot"`: a projectile launch (`Projectile.Launch`).
`tick`; `id` projectile thing id (key of its impact); `shooter`, `side`;
`x`,`z` shooter cell; `target` intended target thing id (empty for a cell
target) and `tx`,`tz` its cell; `dx`,`dz` projectile destination cell
(after aim scatter); `burst` zero-based shot index within the verb burst of
`of` shots; `weapon` weapon def; `proj` projectile def; `tti` ticks to impact
set at launch (`ticksToImpact`, distance / speed).

`k = "impact"`: `Projectile.Impact`. `tick`; `id`; `hit` thing hit (empty:
landed on ground; a non-pawn id: cover or structure); `x`,`z` cell it
stopped in; `dealt` damage the damage ledger attributes to it (total of
ledger rows at the same tick with victim `hit` and instigator the shooter;
not in the bundle's raw file, added at dataset build; two projectiles of one
shooter striking one victim in the same tick share the sum).
A shot with no impact row left the map or was destroyed without impact.

`k = "job"`: `Pawn_JobTracker.StartJob` on a lab pawn. `tick`; `pawn`, `side`;
`job` job def; `forced` player-forced (a Hands order); `thing` target thing id,
`x`,`z` target cell (-1000 when none).

`k = "stance"`: a busy stance entered. `tick`; `pawn`, `side`; `stance`
(`Stance_Warmup` when aiming starts, `Stance_Cooldown` between burst shots and
after a burst); `left` ticks it was set for; `thing`,`x`,`z` focus target.

`k = "draft"`: `Pawn_DraftController.Drafted` set. `tick`; `pawn`; `drafted`.

`k = "think"`: one row per pawn, the first eight ticks of its
`Pawn_JobTracker.JobTrackerTickInterval` calls (the off-camera think) in
`ticks`, and how many calls it had in all in `count`.

Rows that began before the checkpoint was saved (a raider's tick-0 warmup,
its `Wait_Combat`) are not in a resumed run: the stance is loaded from the
save, so raiders' first shot at tick 60 has no stance row. The `think` rows
cover a pawn from the first tick after load.

## Reading it

- Opening: the first `shot` row per `pawn`. Order applied: the tick-0
  `job` rows with `forced`; a mover's `Wait_Combat` start is its arrival.
- Cadence: `shot` rows of one shooter, 3 shots per burst.
- Flight time: impact tick minus shot tick for the same `id`; compare with
  `tti`.
- The map has two non-combat animals (`side` other); they never shoot.
