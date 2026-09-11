# Inspect a failed Docker run

[Documentation](../../README.md) Â· How-to guide

Use this procedure with a retained output directory from `container_checks.py` or
`container_native_acceptance.py`. Preserve the entire directory before rerunning; the
current tools do not yet produce a unified flight-recorder bundle.

## 1. Find the failing stage

Open `result.json` if it exists. Inspect `passed`, the reported error, worker exit codes
and cleanup results. For an early failure without a manifest, read the console output
and `build.log`. A missing report is not a pass.

For fixture tests, open the numbered worker's `pytest.log` and `junit.xml` to find the
failing assertion. For native tests, start with `compose.log` and `container.log` in the
numbered worker directory. Separate an image or startup failure from a scenario that
connected successfully and then failed an assertion.

## 2. Follow native evidence

Inspect `run/Player.log` for game startup and native exceptions. Check
`run/staging.json` and `run/inputs.json` for the staged input identities. Rendered
workers also retain `run/display/` diagnostics; image files alone do not establish that
the expected colony content was rendered correctly.

Use retained controller data and action evidence to distinguish an accepted order from
its verified outcome. For a still-running owned controller, a read-only `GET
/api/diagnostics` returns the latest 100 events for its current colony. That window is
not a full export of the run. The [diagnostic reference](../contracts/diagnostics.md)
lists recording limits and transient evidence.

## 3. Check cleanup and retained checkpoints

Read each worker's `cleanup.log` and the runner's cleanup result before starting another
native attempt. Stop only the failed run's identified container or Compose project if
cleanup did not complete; do not prune other tasks' resources.

If a paired checkpoint was successfully retained, preserve its manifest, native save and
database together. Do not substitute an unrelated save or assume a crash allowed the
game to create a final checkpoint. Follow [save and resume](../../players/save-and-resume.md) for
supported recovery.

## 4. Record a bounded conclusion and rerun

Record the command, source/image identity, input paths or hashes, failed assertion,
observed result, evidence paths and unresolved questions. Use a fresh output directory
for the next run. Rebuild after source changes; `--no-build` reuses old image contents.
Keep explicit repeated attempts rather than hiding crashes behind automatic retries.

The [named native runner](native-scenarios.md) supplies correlated timelines, automatic
exports, rerun arguments and offline fixture capture. Preserve its explicit coverage
gaps rather than inferring unrecorded pawn behavior.
