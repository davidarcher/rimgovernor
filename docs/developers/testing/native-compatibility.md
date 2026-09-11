# Capture native compatibility

This procedure describes the retained compatibility baseline. Current package
checks use `scripts/native_package_acceptance.py` through the scenario launcher;
see the [current Protobuf capability mapping](../../../contracts/native-protobuf-cutover.md).
The old placement wire format below is no longer exported by the current package.

Use this acceptance capture before changing native registration, contracts or saved
component ownership. Start with [prepared private inputs](docker-inputs.md) and
the [scenario launcher](scenario-launcher.md). Build matching production DLLs into
private mod inputs with fixture properties unset. Never replace installed DLLs
while a RimWorld instance is running.

Run from the repository root with a fresh output directory and task-specific image:

```powershell
python scripts/container_scenario.py --game <linux-game> --mods <private-mods> --profile <private-profile> --gabs <linux-gabs> --image rimgovernor-worker:native-compatibility --output .rimgovernor/native-compatibility-01 --timeout 900 -- python scripts/native_compatibility_acceptance.py --root /worker/run --timeout-seconds 600
```

The launcher builds the worker image and publishes its observation dashboard.
Use `--no-build` only with an image already containing the current capture script
and `contracts/domain-inventory.json`. For normal graphical startup, add
`--display xvfb` before the command separator and `--rendered` after it.

For fixture discovery, build separate private inputs with explicit MSBuild fixture
properties. Pass one `--expect-fixture test/name` to the capture command for every
expected installed fixture export. The default rejects fixture exports. Expected
fixtures are discovered but never invoked by this capture. The
[baseline index](../../../contracts/native-compatibility-baseline.json) records
the aggregate build's properties and 47 expected names; six standalone fixture
exports still need separate acceptance.

Inspect both `result.json` and
`run/native-compatibility/compatibility-result.json`. The outer report covers exit
status, cleanup, exported storage and SQLite integrity. The inner report asserts:

- Paginated installed names, no duplicate names, every production export and each
  tool's detail reply, including external RimBridgeServer exports.
- Read-only placement preview, unknown outer argument and missing/null/wrong-type
  placement replies, with paused ticks unchanged during reads.
- Copied native saves with one instance of each of eight GameComponent owners and
  one HomeCoverageState per map, before and after both reload types.
- Stable colony identity, rotated load tokens and the existing allowance of at
  most one tick at each load boundary, including a newly started game process.

The capture uses no model inference. Raw SDK envelopes, three saves and artifact
SHA-256 hashes remain under the output directory. Keep those generated files out
of commits; commit a compact evidence index with source, build, input and image
fingerprints instead.

The legacy binder reports unknown outer keys but accepts them. That observation
does not establish strict request validation. Component census and identity
continuity do not prove populated state-family recovery, branched timeline or
disconnect safety, completed pawn work, or unified-package compatibility. Track
those requirements in [N01](../../BACKLOG.md#n01--unified-rimgovernor-native-mod)
and the [saved-state inventory](../../../contracts/native-state-ownership.md).
