# GABP host notices (vendored fork)

The GABP host is vendored as source in
[`integrations/rimgovernor-host`](../../../rimgovernor-host/src) and shipped in this mod's `Assemblies/`.
It is a hard fork: no upstream tracking; changes from upstream are cherry-picked by hand and the SHA
below updated. Both upstreams are MIT licensed; their notices are retained verbatim beside this file.

| Upstream | Vendored commit | Commit date | Vendored into | Notice |
| --- | --- | --- | --- | --- |
| [pardeike/RimBridgeServer](https://github.com/pardeike/RimBridgeServer) (v2.1.1) | `ca5997c9e02f53609358223a673834e35a1d1ee1` | 2026-08-25 | `src/Host`, `src/Core`, `src/Contracts`, `src/Extensions.Abstractions`, `src/Sdk` | `rimbridgeserver/LICENSE` (Copyright (c) 2025 Andreas Pardeike) |
| [pardeike/Lib.GAB](https://github.com/pardeike/Lib.GAB) (v1.0.5) | `6b3e5e82a10ea12f595ee1a22f9a8489b2f76510` | 2026-08-25 | `src/Gab` | `lib-gab/LICENSE` (Copyright (c) 2025 Andreas Pardeike) |

Only each upstream's `Source/` (RimBridgeServer) or `Lib.GAB/` (Lib.GAB) tree was copied; tests, docs,
skills, workflows and artwork were not.

## Renames

| Upstream | Here |
| --- | --- |
| assembly/namespace `RimBridgeServer`, `RimBridgeServer.Core`, `.Contracts`, `.Extensions.Abstractions`, `.Sdk` | `RimGovernor.Host`, `RimGovernor.Host.Core`, `.Contracts`, `.Extensions.Abstractions`, `.Sdk` |
| assembly/namespace `Lib.GAB` | `RimGovernor.Host.Gab` |
| `RimBridgeServerMod` | `RimGovernorHostMod` |
| Harmony ids `pardeike.rimbridgeserver.*` | `davidarcher.rimgovernor.host.*` |
| mod package id `brrainz.rimbridgeserver` | folded into `davidarcher.rimgovernor.native`; `About.xml` declares `incompatibleWith` it |

Tool names (`rimworld/*`, `rimbridge/*`) are unchanged.

## Deviations from the vendored source

- Target framework is net472 only (upstream also built netstandard2.0 and net10.0 for its tests and NuGet SDK).
- `RimWorldMainTabs.cs` and `RimWorldDebugActions.cs` replace `Range`/`Index` slicing with `Substring`
  (net472 lacks `System.Range`); everything else differs only by the renames above.
- `Assembly-CSharp` is publicised at build time (TaskPubliciser, build-only, not redistributed), as upstream does.

## Packages shipped beside the host

Restored in locked mode from `packages.lock.json`; their runtime DLLs are staged by
`scripts/build_native_mod.ps1` and each needs a `<id>/<version>/LICENSE` here.

| Package | Version | License | Notice |
| --- | --- | --- | --- |
| Newtonsoft.Json | 13.0.4 | MIT | `newtonsoft.json/13.0.4/LICENSE` (verbatim from the package) |
| MoonSharp | 2.0.0 | BSD-3-Clause | `moonsharp/2.0.0/LICENSE` (verbatim from [moonsharp-devs/moonsharp](https://github.com/moonsharp-devs/moonsharp) `master` at `cb4a978093bae3fd7b0b331643a8cd9b6fb8ed16`) |
| Gabp.Runtime | 1.0.0 | Apache-2.0 (package metadata) | `gabp.runtime/1.0.0/LICENSE`: the canonical Apache-2.0 text; the upstream repo ([pardeike/gabp-runtime](https://github.com/pardeike/gabp-runtime) at `ce258707e15bc45e4303e9785ce49bc1fe6506cf`) ships no LICENSE file |
