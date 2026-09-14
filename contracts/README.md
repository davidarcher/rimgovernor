# Migration coverage

The original inventory rows describe the Python comparison surface for G01;
`domain-inventory.json` also contains the repository-owned N01 native source
baseline. These are not runtime capability declarations. `source_revision` records
the retained Python comparison revision. Source locators track the current checkout;
locator-only refreshes do not recapture or change fixture provenance;
`status` remains pending until the owning chunk's behavioral acceptance passes.

- `domain-inventory.json`: module classification, semantic/action/completion kinds
  and native tool call sites.
- `interface-inventory.json`: HTTP/events, configuration, launchers and media.
- `state-inventory.json`: SQLite, checkpoint and recovery boundaries.
- `fixtures/`: small sanitized comparison cases with source provenance. Synthetic
  fixture evidence does not establish a native outcome or model interpretation.
  `state-baseline.json` retains seven representative cases;
  `serialization-baseline.json` retains exact Python JSON bytes/signatures and
  uncertain progress. Its self-test rejects a deliberately changed signature.

Each row identifies its source, target Go package, owning G01 chunk, existing
fixture checks and native scenario references. Empty evidence lists preserve an
uncovered acceptance requirement; they do not waive it. Tooling classifications
identify code that may remain Python after the production cutover.

`domain-inventory.json`/`interface-inventory.json`/`state-inventory.json` and
`scripts/check_go_coverage.py`, which validated their structure and each row's
Python source path, tracked G01 migration coverage against the Python source
tree. That tree was removed in G01.13
([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)), so nearly
every row's `source`/`fixtures`/`native_scenarios` reference is now dangling;
`check_go_coverage.py` is retained as a script but is no longer run in CI and
is not expected to pass. The inventories are a frozen historical migration
record, like the baselines below, not a live coverage gate. See the
[rewrite sequence](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AG01%22)
for what remains.

## Generated contracts

The [schema generation contract](schema-generation.md) defines canonical inputs,
strict boundary rules and the versioned output manifest. G01.02 sequences generated
models, native adapter wiring and actual SDK acceptance as separate gates.

## Timing baseline

The [Python baseline](python-baseline.json) records a bounded uncontended headless
sample, source/input/image hashes and retained artifacts. It includes partial
construction holds and a native warning pause. Observed pawn work and new buildings
are not attributed to controller actions; recovery and sustained outcomes remain
separate acceptance gates. Measurements alongside another native scenario cannot
establish an uncontended comparison.

The Python throughput-measurement procedure this baseline was captured with was
removed in G01.13 ([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)),
along with `check_serialization_baseline.py`/`check_state_inventory.py`, its only
regeneration tooling. `python-baseline.json`,
`fixtures/state-baseline.json` and `fixtures/serialization-baseline.json` are
therefore frozen historical artifacts: retained for comparison, not
regeneratable until equivalent Go tooling exists
([issue #38](https://github.com/davidarcher/rimgovernor/issues/38)). No
performance or production cutover acceptance is implied by inventory validation.

## Native compatibility baseline

[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22) shares these
inventories with G01. [Native reply examples](fixtures/native-replies-baseline.json)
retain three actual legacy Linux SDK request/reply pairs: identity, a single
placement preview and a spatial-access refusal. Each includes source line/file
hashes and the measured input hashes from the Python baseline. SDK text strings
and structured results are preserved, including nulls and operation metadata.
The refusal illustrates why transport success is not native operation success.

These are historical comparison fixtures, not fresh discovery, save/reload
acceptance or completed pawn work. The recorded source revision and artifact root
identify the original run. Unified-package parity remains open.

The [executable legacy baseline](native-compatibility-baseline.json) indexes fresh
production batch and graphical startup, plus aggregate fixture discovery. It
retains artifact hashes for actual SDK replies and copied-save reloads. The
Python capture procedure it was recorded with was removed in G01.13
([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)); like the
other baselines above, it is a frozen historical artifact until equivalent Go
tooling exists ([issue #38](https://github.com/davidarcher/rimgovernor/issues/38)).
Component presence and identity continuity do not establish populated field recovery.

The domain inventory's `native_surface` extension records exported declarations,
project membership, normalized source fingerprints, compilation exclusions and
production/fixture ownership. It has separate native provenance and links back to
existing G01 rows. `python scripts/check_native_inventory.py --check` runs its
drift checker directly (it no longer runs through `check_go_coverage.py`, which
short-circuits on the now-dangling G01 rows above); source extraction does not
establish installed SDK availability or exhaustive implemented argument variants.

- [Implemented operation variants](native-operation-variants.md) records selectors,
  defaults, aliases and dry-run behavior for all 55 production exports.
- [Mutable static state](native-static-state.md) records lifecycle ownership and
  source-identified invalidation hazards.
- [Saved-state ownership](native-state-ownership.md) records exact persisted keys,
  proposed owners and reconstruction/disconnect constraints.
- [Runtime and packaging](native-runtime-packaging.md) records patch/loader/build
  boundaries, deployment consumers and provenance gaps; its
  [source index](native-runtime-source-index.json) retains lexical anchors and
  normalized hashes for all inspected native and fixture sources.

The unified package now has fresh-game batch and graphical acceptance through
`scripts/native_package_acceptance.py`. Legacy captures are optional diagnostic
references, not compatibility gates. Remaining typed/native ownership and platform
work stays in N01; all development state may start fresh.
