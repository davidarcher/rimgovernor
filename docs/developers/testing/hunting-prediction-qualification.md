# Hunting prediction qualification protocol

[Encounter contract](../contracts/encounter-prediction.md) · [Combat research](../architecture/combat-predictability-research.md)

`hunt-native-v1` extends CombatLab's existing case runner, sweep dispatcher and
retained result bundles. It compares native game ticks with independently seeded
hypothetical worker predictions. Worker Verify replay is a separate fidelity
check and never enters the independent-observation denominator.

## Frozen pilot and collection

The six registered `combatlab/hunting-prediction-*` cases cover `baseline`,
`health`, `distance`, `terrain`, `herd` and `departure`. Each prepares a 100-cell
lab with an ordinary bolt-action rifle, a 30-year-old Baseline hunter with skill
8, and a three-year-old wild boar. Variations supply an observed arm cut, greater
distance, a sand strip, three wild boars, or a second hunter leaving at tick 300.
All initial health, equipment and terrain observations enter the request. The lab holds temperature and its observed sky glow constant during the bounded encounter; changing environmental conditions are outside this class. The
native encounter has a 6,000-tick horizon. Incidents are quiet, but wild-animal
acceleration is disabled and revenge uses the game's Rough difficulty factor.
No permanent manhunter substitutes for revenge.

Pilot run seed 1 is calibration only. A broader collection is a separate,
explicitly authorized/nightly run: 30 calibration runs (`-seeds 100,...,129`)
and 60 held-out runs (`-seeds 10000,...,10059`) in each stratum, 540 native
encounters total. The native future seed includes a distinct stratum offset;
the predictor uses seed 1000001, independent of every native seed. Repeated
pilot seed 1 is never counted as new independent evidence. Freeze this protocol
and source/build/mod manifests before collection. Any changed implementation
requires a new collection; do not pool incompatible provenance.

Thirty calibration observations per stratum fit a split-conformal duration
interval. The score is absolute native duration error divided by predicted
duration (minimum denominator one tick). Its `ceil(.9*(n+1))` ordered training
residual supplies the radius around the predicted median. At least 19 uncensored
calibration observations are required; missing calibration or a censored
prediction remains unresolved. Held-out outcomes never fit their own interval.
This allows one online sample without calling that sample its own confidence
interval. Exchangeability is an assumption tested by held-out coverage, not a
property established by engine replay.

One sample is the initial bounded estimate, not a safety guarantee. Source mod
identity and hypothetical-worker mod identity are pinned separately: the live
fixture includes the Governor and Harmony, while the prediction-only worker
loads official content without starting a second Governor. The worker's exact
content fingerprint enters engine identity. Qualification must review this
specific source-to-predictor pair; a changed fingerprint invalidates its scope.

## Hunter-count and prey variants

The fixture tool also accepts `animal`, `preyCount` and `hunters` under the ad hoc `probe` stratum, for scenario
studies that are not part of the six qualification strata. Two or more hunters are drafted and focus-fire (see the
[encounter contract](../contracts/encounter-prediction.md)); the worker arena does the same, so a group request is
modelled by the same mechanism as the native trial.

Bounded pilot (3 native seeds per cell, native only; not qualification): against a grizzly, rhinoceros or elephant, revenge
fired in every trial and one rifleman was downed in most, killed in some; three drafted riflemen killed the prey in about
330-920 ticks versus 1000-5800 for one, with fewer deaths. A five-boar herd showed no harm and no clear time difference.
Native duration is heavy-tailed (123-6000 ticks for one hunter and one boar), so no predictor can reach a 20% per-trial
median duration error: the best constant per stratum has 29-51% on the calibration data. Treat that gate as unreachable
as written until it is revised.

## Scoring and stopping

Serious harm means hunter death/downing, a new missing part, or a new/worsened
injury that requires tending, bleeds or is permanent. Existing observed injuries
are the baseline, not newly charged damage. A false-safe event is observed
serious harm where all requested prediction samples show none. Missed deaths
and contribution after a declared departure fail independently.

Qualification requires the previously declared median duration error ≤20%,
90% held-out coverage of declared 90% intervals, correct ordering for separated
observed duration envelopes, zero false-safe observations, and the exact
one-sided 95% zero-failure upper bound ≤1%. At least 299 independent applicable
safe predictions are necessary. The planned 360 held-out encounters might not
supply that denominator; never relabel abstentions or calibration observations
to make up the difference. Actual revenge must be observed. Report rejected,
unresolved and horizon-censored trials, not only successful rows.

Stop collection on reconstruction/provenance mismatch, missed serious harm,
continued contribution after departure, process-cleanup failure or a systemic
runner failure. Diagnose and fix before spending the remaining run budget.
The shared dispatcher cancels active and queued hunting trials after the first failure and does not start another stratum. Ordinary combat sweeps retain their continue-on-failure behavior.
Runtime gates remain 15-second cold start, 1-second warm request and 2-second
cancellation/cleanup. Diagnostic ceilings record failures; they do not relax
these gates. Measure shared machine load with timing evidence.

## Commands and artifacts

Build the existing realcombat Host in Release and install the private native
fixture with `acceptance setup -rebuild -fixture DebugStartFixture`. A single
pilot uses normal `acceptance run combatlab/hunting-prediction-baseline`, with
an absolute private `-root` and explicit `-run-seed 1`.

The existing `acceptance sweep` accepts `hunting-prediction-baseline` (and the
other five suffixes). Its normal `-seeds`, `-workers`, root isolation and retained
child bundles apply. Collection does not alter committed combat baselines.
Aggregate retained results with:

```text
RimGovernor.cmd acceptance sweep -hunting-report <absolute-output-directory> <absolute-report.json>
```

The report retains every trial and missing artifact. `AccuracyPassed` alone is
not an accepted qualification: reconstruction, runtime, scope and independent
review remain required. No accepted report currently exists. #3129 remains open
until a qualified usable capability exists; #3130 must not consume pilot results.
