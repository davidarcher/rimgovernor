# Verify waste hauling and burial

[Documentation](../../README.md) · [Waste contracts](../contracts/waste-management.md)

Use a disposable local Docker worker with [staged Linux inputs](docker-inputs.md).
Build the task's colony bridge with `-p:WasteFixture=true`, in addition to the Linux
reference paths in that guide, and copy its DLLs into a private mod snapshot. This
flag includes `test/waste_fixture` for setup; production builds omit it. Never
replace DLLs in an installation used by a running game.

Build a task-specific worker image using `containers/Dockerfile --target worker`.
Run it with fresh writable output and the native inputs mounted read-only. Pass
the ordinary `rimgovernor.container_worker` input arguments and this command:

```text
-- python /app/scripts/waste_acceptance.py
```

The default case prepares a rotten animal corpse, an explicitly unwanted item,
protected possessions and separated native stockpile storage. It submits the
maintained goal through player admission, compiles the deterministic method,
executes with Hands and waits for actual pawn delivery. It verifies that receipt
acceptance remains waiting and that relocated items still exist.

Run a second fresh worker with Docker environment `RIMGOVERNOR_WASTE_BURIAL=1` to prepare
a named colony corpse and an accepting grave. The case verifies protection without
burial authority, explicitly requests burial, observes the exact body inside its
grave and refuses exhumation. Setup may create test objects; acceptance actions use
ordinary native pawn jobs and unchanged game rules.

Inspect `run/waste-result.json` and require `passed: true`, alongside exit code 0.
Retain `run/inputs.json`, `run/staging.json`, `run/HeadlessPlayer.log`, the SQLite
state and every failed attempt. These are scripted native hauling/burial checks,
not model interpretation, incineration or long-term hazardous-waste acceptance.

Run `controller_tests/test_waste_management.py` for exact-ID, unknown-state,
burial-versus-relocation, load-race, bounded-method and refusal regressions, then
the [Docker controller suite](docker-checks.md).
