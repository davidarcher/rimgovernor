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

Build against the installed game/SDK references, then use the standard isolated
scenario launcher with `scripts/native_package_acceptance.py`. It creates a new
game, checks production discovery and the loader/component boundaries, previews
without advancing paused time or changing the camera, and verifies the startup
mode from native logs. Run batch and graphical/Xvfb modes against the same package.
Current-game safety and gameplay changes need their own native outcomes; no
old-save import or historical byte-parity campaign is required.

See [N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22) for remaining typed
contracts, lifecycle ownership, persistence and supported-platform acceptance.
