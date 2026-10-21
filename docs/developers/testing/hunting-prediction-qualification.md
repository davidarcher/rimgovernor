# Hunting prediction qualification protocol

[Encounter contract](../contracts/encounter-prediction.md) · [Combat research](../architecture/combat-predictability-research.md)

`hunt-native-v2` asks the question the hunting planner (#3130) needs answered: given the same prey, does
the predictor rank a hunter set (one, two or three drafted riflemen) the way the real game does, in
severe harm to the hunters and in whether the prey ends downed? It extends CombatLab's case runner, sweep
dispatcher and retained result bundles. Worker Verify replay is a separate fidelity check and never
enters the independent-observation denominator. (`hunt-native-v1` scored per-trial duration error against
conformal intervals; native duration is heavy-tailed, so that gate was unreachable and did not measure the
planner's decision. It is retired.)

## Grid

Fifteen cells: prey `boar`, `grizzly`, `rhinoceros`, `elephant` or a five-boar `herd`, each with 1, 2 or 3
hunters (cell id `<prey>-h<n>`). Each runs in the 100-cell quiet lab with ordinary bolt-action rifles,
skill-8 Baseliners and three-year-old wild prey; revenge uses the game's Rough
`manhunterChanceOnDamageFactor`; horizon 6,000 ticks. Two or more hunters are drafted and focus-fire
(see the [contract](../contracts/encounter-prediction.md)); the worker arena does the same.

Per cell, `acceptance sweep hunting-grid-<cell>` first makes **one** predictor request of 16 samples
(independent seeds 1000001–1000016, none shared with a native seed) on the scene observed natively under
seed 1, and measures cold start, the warm request and cancellation; it then runs one native encounter per
sweep seed;
collection uses 30 held-out seeds per cell (`10000..10029`), 450 native trials, with no calibration
split because no fitted interval is used. Native seed is `cellIndex*100000 + seed`. Freeze protocol and
build/mod manifests first; a changed implementation needs a new collection.

## Criteria (per cell unless stated)

- **Severe harm** (hunter death, downing, new missing part, or new/worsened tendable, bleeding or permanent
  injury; existing injuries are the baseline) and **prey downed** (every prey dead or downed before the
  horizon): the predicted rate over the 16 samples is within ±0.2 of the native rate over ≥30 trials.
- **Duration**: the native median lies within the predictor's interquartile range widened by a factor of 2.
- **Ranking** (across cells): for cell pairs whose native severe-harm rates are separated (non-overlapping
  95% Wilson intervals), the predicted harm order agrees strictly; zero inversions, and at least one
  separated pair.
- **False-safe**: observed serious harm in a cell whose 16 samples showed none. Zero observed, and the exact
  one-sided 95% zero-failure bound `1 - 0.05^(1/n)` ≤ 1%, which needs ≥299 native observations in cells
  predicted safe. A grid with fewer such observations reports `false_safe_bound`; abstentions and
  calibration are never relabeled to fill the denominator.
- **Missed death** and revenge never observed fail; prey retaliation must actually be observed.
- **Runtime**: cold start ≤15 s, cancellation ≤2 s, and the 16-sample warm request ≤20 s (≤1 s per sample),
  consumed asynchronously by the planner.
- **Provenance**: native request equals the predicted request except the observation tick; identical
  build/mod/engine identity; native initial state matched.

The pass/fail thresholds are research criteria, not colony risk weights.

## Stopping

Stop collection on reconstruction/provenance mismatch, missed death, process-cleanup failure or a systemic
runner failure; diagnose and fix before spending more budget. The shared dispatcher cancels queued hunting
trials after the first failure. Retain rejected and unresolved trials.

## Commands and artifacts

Build the existing RealCombat Host in Release and install the native fixture with
`acceptance setup -rebuild -fixture DebugStartFixture,FreezeNeedsFixture,LetterFixture,QuietStorytellerFixture`.
Run a cell with `acceptance sweep hunting-grid-<cell> -workers 1 -seeds a,b,c -root <abs> -output <abs fresh dir>`
(one cell per call, fresh output directory each). The cell directory holds `prediction/hunting_cell_prediction.json`
and, per seed, `seed-<n>/native_trial.json` and `seed-<n>/hunting_trial.json`. Aggregate with:
Aggregate with:

```text
RimGovernor.cmd acceptance sweep -hunting-report <absolute-output-directory> <absolute-report.json>
```

The report retains every cell prediction, trial and missing artifact. `AccuracyPassed` alone is not an
accepted qualification: reconstruction, runtime, scope and independent review remain required. The run of
2026-10-17 was accepted with its known limits recorded in
[encounter prediction](../contracts/encounter-prediction.md#accepted-evidence-and-known-limits) (#3124); the
served hunt comparison no longer runs the predictor: it estimates analytically (`HuntEstimator`, #3533), and
the sweep is that estimate's offline check. A cell is re-run on demand, by name and locally; the remote
workflows do not run it (#3515).
