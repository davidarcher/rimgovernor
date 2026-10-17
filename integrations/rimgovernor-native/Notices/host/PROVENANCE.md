# Host notices (vendored fork)

The host is vendored as source in
[`integrations/rimgovernor-host`](../../../rimgovernor-host/src) and shipped in this mod's `Assemblies/`.
It is a hard fork: no upstream tracking; changes from upstream are cherry-picked by hand and the SHA
below updated. The upstream is MIT licensed; its notice is retained verbatim beside this file.
| Upstream | Vendored commit | Commit date | Vendored into | Notice |
| --- | --- | --- | --- | --- |
| [pardeike/RimBridgeServer](https://github.com/pardeike/RimBridgeServer) (v2.1.1) | `ca5997c9e02f53609358223a673834e35a1d1ee1` | 2026-08-25 | `src/Host`, `src/Core`, `src/Contracts`, `src/Extensions.Abstractions`, `src/Sdk` | `rimbridgeserver/LICENSE` (Copyright (c) 2025 Andreas Pardeike) |

Only the upstream's `Source/` tree was copied; tests, docs,
skills, workflows and artwork were not.

## Renames

| Upstream | Here |
| --- | --- |
| assembly/namespace `RimBridgeServer`, `RimBridgeServer.Core`, `.Contracts`, `.Extensions.Abstractions`, `.Sdk` | `RimGovernor.Host`, `RimGovernor.Host.Core`, `.Contracts`, `.Extensions.Abstractions`, `.Sdk` |
| `RimBridgeServerMod` | `RimGovernorHostMod` |
| Harmony ids `pardeike.rimbridgeserver.*` | `davidarcher.rimgovernor.host.*` |
| mod package id `brrainz.rimbridgeserver` | folded into `davidarcher.rimgovernor.native`; `About.xml` declares `incompatibleWith` it |

Tool names were `rimworld/*` and `rimbridge/*` upstream; here they are `rimgovernor/*` (#2059).

## Deviations from the vendored source

- Target framework is net10.0 only (upstream also built netstandard2.0 and net10.0 for its tests and NuGet SDK).
- Pruned to what RimGovernor calls (#2056): the UI workbench, Lua/script runner and MoonSharp, virtual pointer,
  map click injector, architect, context menus, view/screenshot, DPA, input, selection, notification, mod
  settings/configuration and debug-action modules, the SDK tool client, game clock and evidence helpers, and 118
  of the 125 built-in tools. What remains of the vendored `Host` project is the `rimgovernor/*` host-owned
  tools the brain calls plus the host core; see `docs/developers/source-map.md`.
- `Assembly-CSharp` is publicised at build time (TaskPubliciser, build-only, not redistributed), as upstream does.

## Packages shipped beside the host

Restored in locked mode from `packages.lock.json`; their runtime DLLs are staged by
`RimGovernor.cmd native-build` (`NativeModBuild`) and each needs a `<id>/<version>/LICENSE` here.

| Package | Version | License | Notice |
| --- | --- | --- | --- |
| Newtonsoft.Json | 13.0.4 | MIT | `newtonsoft.json/13.0.4/LICENSE` (verbatim from the package) |
