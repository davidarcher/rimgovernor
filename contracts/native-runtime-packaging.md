# Native runtime and package ownership

The active package is `integrations/rimgovernor-native`, installed as
`Mods/RimGovernor` with package ID `davidarcher.rimgovernor.native`.
The [source index](native-runtime-source-index.json) and shared
[domain inventory](domain-inventory.json) track current production/fixture sources.
Historical captures do not gate active development; use new disposable state.

## Build and loading

| Owner | Source / output |
| --- | --- |
| Early native runtime | `src/Runtime/RimGovernor.Runtime.csproj`; persistence and batch-gated headless startup; `Assemblies/RimGovernor.Runtime.dll`. |
| SDK tool adapter | `src/Bridge/RimGovernor.Bridge.csproj`; references Runtime; `BridgeTools/RimGovernor/RimGovernor.Bridge.dll`. |
| Package builder | `scripts/build_native_mod.ps1`; one About manifest, private build inputs, fresh staging, notices/source and artifact manifest. |
| Native consumers | `headless.py`, `container_worker.py`, `campaign_manifest.py` and scenario launchers use the unified package in batch and graphical modes. |

Both assemblies target net472 with warnings as errors. Runtime is loaded early
for Verse component discovery and startup hooks; the SDK loads tools separately.
Normal startup leaves headless suppression inactive. Fixture build properties
include only explicitly requested test sources and must produce separately
identified artifacts. Production discovery must contain no test tools.

The old projects and excluded duplicate PawnSettingsRead/StockpileFilter sources
are removed. Active helper implementations remain in PawnConfigTool/ZoneCellsTool.
Native state is still owned by its current runtime components until N01.06 wires
replacement consumers; assembly consolidation alone does not move bookkeeping
into SQLite or establish typed contracts.

## Provenance

[Headless provenance](../integrations/rimgovernor-native/Notices/headless/PROVENANCE.md)
pins the retained upstream revision. Its
[GPL-3.0 notice](../integrations/rimgovernor-native/Notices/headless/LICENSE), source
and build instructions remain in the package.
[Companion provenance](../integrations/rimgovernor-native/Notices/companion/PROVENANCE.md)
records attribution and unresolved redistribution permission; combining sources
does not grant new rights. Resolve that before public redistribution. Keep licensed
game and external SDK/Harmony binaries out of the package.

## Acceptance

Build against the installed game/SDK references. The Python scenario launcher and
`scripts/native_package_acceptance.py` this section once described (new-game
production discovery, loader/component boundary checks, paused preview
verification and batch/graphical startup mode checks) were removed with the
rest of the Python acceptance toolchain in
[G01.13](https://github.com/davidarcher/rimgovernor/issues/33); equivalent Go
coverage is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).
Current-game safety and gameplay changes need their own native outcomes; no
old-save import or historical byte-parity campaign is required.

See [N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22) for remaining typed
contracts, lifecycle ownership, persistence and supported-platform acceptance.
