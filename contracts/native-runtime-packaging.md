# Native runtime and package inventory

N01.00 source baseline: `3fea7d5c` (2026-09-10). This inventory records existing
source and packaging behavior. It does not establish unified-package acceptance.
The [source index](native-runtime-source-index.json) records all 117 tracked C#
files in the two native integrations and `scripts/fixtures`, their LF-normalized
UTF-8 source hashes,
compilation category, migration owner and lexical static/reflection/patch anchors.
Anchors are one-based lines at that revision. They include method declarations
and reflection helpers; they are navigation evidence, not a C# semantic analysis.
Each row's uncovered acceptance remains open in [N01](../docs/BACKLOG.md#n01--unified-rimgovernor-native-mod).

Tool and saved-field inventories are separate concerns; retain G01's existing
[domain](domain-inventory.json), [interface](interface-inventory.json) and
[state](state-inventory.json) inventories as the Python call-site baseline.
No source, dependency, loader path or default launcher changes in this slice.

## Compilation and loader boundaries

All paths below are repository-relative. N01.01's native integrator owns package
migration; N01.07 and G01's launcher/release owner own consumer cutover.

| Current input | Compiled output / constraint | Uncovered acceptance |
| --- | --- | --- |
| `integrations/colony-bridge/src/ColonyObservations.csproj` | `net472`, `LangVersion=latest`, warnings as errors, no debug symbols; `BridgeTools/Observations/RimGovernor.Observations.BridgeTools.dll`. SDK extension loader owns tool registration. | Reproducible pinned compiler and dependency hashes; one SDK registration per tool after package move. |
| `integrations/colony-bridge/src/identity/ColonyIdentity.csproj` | Separate `net472` project; `Assemblies/RimGovernor.ColonyIdentity.dll`; `Private=false` project reference from bridge. No explicit warnings-as-errors or nullable setting in this project. | Preserve saved CLR identities and one component instance after old-save load; scoped nullable/compiler gates. |
| `integrations/headless-rim/src/HeadlessRim.csproj` | `net472`; `Assemblies/HeadlessRimPatch.dll`; no explicit warnings-as-errors or nullable setting. | Normal and batch initialization; fresh artifact identification and reproducibility. |
| `integrations/colony-bridge/About/About.xml` | Package `davidarcher.rimgovernor.observations`, RimWorld 1.6; declares dependency/load-after `brrainz.rimbridgeserver`. | Unified identity and explicit dependency/load-order checks; mixed old/new package rejection. |
| `integrations/headless-rim/About/About.xml` | Package `RedEyeDev.HeadlessRim`, version 0.1.0, RimWorld 1.6; no dependency declaration. | Explicit Harmony dependency/load order in unified package. |

**Compilation exclusions must survive the move until deliberately removed.**
`ColonyObservations.csproj` removes `identity/**/*.cs` because that source belongs
to its own assembly. It also removes `PawnSettingsRead.cs` and `StockpileFilter.cs`.
The compiled duplicate classes live inside `PawnConfigTool.cs` and
`ZoneCellsTool.cs`, respectively. A filename search alone does not identify the
active implementation. N01.01 owns comparison before removal; N01.08 owns the
final caller/fixture verification. The source index retains both excluded files.

The bridge conditionally includes fixture sources from `scripts/fixtures`:

| Build property | Included source stem(s), all with `.cs` suffix |
| --- | --- |
| `ThroughputFixture` | ThroughputFixture; also defines `THROUGHPUT_FIXTURE` |
| `WasteFixture`, `DisasterFixture`, `GearFixture`, `MoodFixture`, `PopulationFixture`, `HusbandryFixture` | Matching property name |
| `MedicalManagementFixture` | MedicalManagementFixture |
| `MiningFixture` | MiningFixture, DeepMiningFixture |
| `FoodObservationFixture` | FoodObservationFixture |
| `UpkeepFixture` | UpkeepFixture, HomeCoverageFixture, SleepingFixture, StoreroomFixture, WallUpgradeFixture, MedicineFixture, AnimalContainmentFixture, AnimalFeedFixture, ComfortFixture |
| `TradeFixture`, `ScenarioStartFixture`, `ConstructionLedgerFixture`, `EmergencyDevelopmentFixture`, `InstallFixture`, `ForecastFixture`, `InspectorFixture`, `ModalFixture`, `CampaignMetricsFixture` | Matching property name |

Other fixture files are not implicitly compiled by the bridge's directory glob;
for example `InterruptionFixtures.csproj` builds a separate acceptance assembly.
The index's `fixture-only` category is not a claim that every file has a bridge
property. N01.01 must prove production discovery excludes every fixture tool and
give fixture builds distinct artifact identities; current conditional properties
do not change the bridge assembly name/output path.

## Dependency and deployment consumers

Each native project pins `Microsoft.NETFramework.ReferenceAssemblies.net472`
1.0.3 as a private build package. Game references use externally supplied
`RimWorldManagedDir`; Harmony uses `HarmonyAssembly`. The bridge also uses
`RimBridgeSdkDir` for `RimBridgeServer.Sdk.dll` and `Newtonsoft.Json.dll`.
These assembly references have `Private=false`; installed game/SDK/Harmony
binaries are not copied by these project references. Unity dependencies span
CoreModule, ImageConversionModule, ScreenCaptureModule and IMGUIModule in the
bridge, and AudioModule/CoreModule in headless. The identity project references
Assembly-CSharp only. The project files do not pin installed binary hashes.

| Consumer | Current responsibility | Owner and uncovered acceptance |
| --- | --- | --- |
| `scripts/build_observation_bridge.ps1` | Release build; optionally copies Assemblies/About/BridgeTools into `Mods/RimGovernorObservations`. | N01.01/07: fresh staging, complete manifest, atomic activation, old/new detection. |
| `scripts/build_headless.ps1` | Release build; optionally copies Assemblies/About/LICENSE into `Mods/RimGovernorHeadless`. | N01.01/07: corresponding source/provenance packaging and same staging gates. |
| Both PowerShell builders | Default `tmp/dotnet/dotnet.exe`; reject install while a `RimWorldWin64` process exists. | N01.07: all supported process names/platforms and isolated peer survival; existing Windows guard is not cross-platform validation. |
| `controller/rimgovernor/headless.py` | Windows private profiles and installed native mod paths. | N01.07 + G01 launcher: fresh install, copied profile, rollback. |
| `controller/rimgovernor/container_worker.py` | Linux mod/profile staging and native project builds. | N01.01/07: fixture property propagation, dependency hashes and batch/rendered package parity. |
| `controller/rimgovernor/campaign_manifest.py` | Records expected native artifact/source identity. | N01.07 + G01 release: manifest format and mixed-generation rejection. |
| `controller/rimgovernor/container_input_cache.py`, `scripts/container_input_cache.py` | Cache/verify licensed input snapshots used by workers. | N01.07: invalidation on package/dependency changes, stale cache diagnostics. |
| `containers/Dockerfile` | Test image copies integrations/scripts; native stage inherits test content. | N01.07: complete source/notices and reproducible package inputs; rebuild old images. |
| `scripts/prepare_bridge_trial.py`, `scripts/prepare_scenario.py`, `scripts/test_install.ps1` | Prepare profiles/install checks using legacy package paths. | N01.07: loader paths and complete package checks. |
| `scripts/container_world_progression_acceptance.py`, `scripts/native_interruption_acceptance.py`, `scripts/native_tick_budget_acceptance.py`, `scripts/waste_acceptance.py` | Direct native source/project/package references in specialized acceptance. | N01.07: fixture staging and DLL identity checks against unified output. |
| `scripts/container_scenario.py`, `scripts/container_native_acceptance.py` and callers | Shared runner/dashboard lifecycle, including indirect worker staging. | N01.07: standard automatic loopback dashboards, native normal/batch/rendered acceptance. |

Direct references were searched across tracked Python, PowerShell and container
sources. Indirect scenario consumers must follow the shared worker/runner; this
table supplements G01's launcher inventory rather than duplicating its call sites.

## Patch ownership

N01.05's native runtime owner coordinates patch registration, required/optional
health and cleanup. N01.04's operation owner owns guard semantics; N01.06's state
owner owns the evidence those guards preserve. Every row is **inventoried,
unmigrated**. Existing scenario entry points below are test candidates, not evidence
that these tests have passed on a unified artifact.

| Source / current Harmony owner | Native targets | Acceptance still required for migration |
| --- | --- | --- |
| `SupervisedPlayTool.cs` / `homebridge.supervised-play` | TickManager.TickManagerUpdate, DoSingleTick | Lease expiry, stale load/direction, clock restoration, lost controller and injected patch failure. |
| `CombatInjuryHook.cs` / `homebridge.combat-injury-edge` | Thing.TakeDamage | Injury edge exactly once and supervision stops after patch failure. |
| `LetterPauseHook.cs` / `rimgovernor.letter-pause-source` | LetterStack.ReceiveLetter, TickManager.CurTimeSpeed setter, TogglePaused, Pause | Same-frame letter/manual precedence; unexpected interruptions remain failures. |
| `ConstructionLineage.cs` / `rimgovernor.construction-lineage` | Blueprint_Build.MakeSolidThing; Frame.CompleteConstruction/FailConstruction; GenSpawn.Spawn overload | Ordinary construction lineage, failure and fresh reload without duplicate work. |
| `HaulTracking.cs` / `rimgovernor.haul-tracking` | Thing.TryAbsorbStack/SplitOff/Destroy; GenSpawn.Spawn overload | Split/merge/loss versus actual delivery, disconnect and branched saves. |
| `DraftOwnership.cs` / `rimgovernor.draft-ownership` | Pawn_DraftController.Drafted setter | Player changes invalidate ownership; cleanup never overwrites later player choice. |
| `RecoveryAreaOwnership.cs` / `rimgovernor.recovery-area-ownership` | Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap setter | Same ownership rule for areas across load/disconnect. |
| `DrillingGuard.cs` / `rimgovernor.bounded-drilling` | CompDeepDrill.CanDrillNow/DrillWorkDone; Blueprint.TryReplaceWithSolidThing; Frame.CompleteConstruction | Ordinary output budget and guard failure/disconnect behavior. |
| `MiningGuard.cs` / `rimgovernor.mining-safety` | JobDriver_Mine.DoDamage; DesignationManager.RemoveDesignation | Native extraction, player removal and budget refusal without extra damage. |
| `ProductionPolicyTool.cs` / `rimgovernor.production-policy` | WorkGiver_DoBill.TryFindBestBillIngredients; Toils_Recipe.FinishRecipeAndStartStoringProduct | Actual consumed/output quantities, filter restoration on exception and disconnected work. |
| `WallUpgradeTool.cs` / `rimgovernor.wall-upgrade` | WorkGiver_Deconstruct.HasJobOnThing; JobDriver_Deconstruct.FinishedRemoving/MakeNewToils; DesignationManager.AddDesignation | Supported replacement, player designation changes and removal failure. |
| `HomeCoverageTool.cs` / `rimgovernor.home-coverage` | Area_Home.Set; Area.Clear/Invert | Player exclusions and revision guards across reuse/load. |
| `OrderedWorkHistory.cs` / `rimgovernor.ordered-work-history` | Pawn_JobTracker.TryTakeOrderedJob; Pawn_DraftController.GetGizmos | Ordered-job evidence and player/UI ownership across reuse. |
| `PawnImageTool.cs` / `davidarcher.rimgovernor.pawn-images` | Game.UpdatePlay; CameraDriver.CurrentViewRect getter | Frames, exception-safe camera restoration, no tick/selection change. |
| `PlayerInputTool.cs` / `rimgovernor.player-frame-ui` | Root.OnGUI | Fresh UI revision, stale frame refusal and private display isolation. |
| `RenderDemandTool.cs` / `davidarcher.rimgovernor.render-demand` | MapDrawer.DrawMapMesh | Lease expiry/minimization/render restoration without simulation change. |

Headless `HeadlessRimMod` installs `com.headlessrim.core` startup patches only
when command-line arguments contain `-batchmode`. `com.headlessrim.bootstrap`
postfixes `UIRoot_Entry.Init`, then applies runtime patches. `HeadlessStartup`
separately disables vSync/frame cap behind the same batch gate. The current menu
postfix has no explicit once-only guard; repeated reuse and duplicate installation
are N01.05 acceptance requirements.

`HeadlessPatches.cs` startup targets are Designator_Build.UpdateIcon,
ResolutionUtility.Update, GlobalTextureAtlasManager.TryInsertStatic and
Graphic_Single/Graphic_Multi/Graphic_Collection.TryInsertIntoAtlas. Runtime targets
are UIRoot_Play.UIRootUpdate, UIRoot_Entry/UIRoot_Play.UIRootOnGUI,
MapInterface.MapInterfaceOnGUI_BeforeMainTabs, LongEventHandler.LongEventsOnGUI
and LongEventsUpdate, WorldFeatures.UpdateFeatures, Section.RegenerateAllLayers,
MapDrawer.WholeMapChanged/SectionChanged/RegenerateEverythingNow/
MapMeshDrawerUpdate_First/DrawMapMesh/Dispose, Graphic.Print,
SoundStarter.PlayOneShot, SoundRoot.Update, MusicManagerPlay.MusicUpdate,
MusicManagerEntry.MusicManagerEntryUpdate and an explicit PortraitsCache.Get
overload. Most prefixes suppress presentation. LongEventsUpdate instead marks the
private `alreadyDisplayed` field and permits native execution; Dispose permits
native cleanup when drawing collections exist. Missing targets may be skipped;
patch exceptions are logged rather than represented in typed capability health.
N01.05 must verify normal startup installs none of these patches, and batch
long events/autosaves and normal pawn work survive required-target failures.

## Static lifecycle and reflection boundaries

The source index includes all static tokens, including thread-local patch
contexts, registration flags and readonly caches. Saved-field ownership belongs
to N01.00's state audit; a static cache must not become authoritative save state.

| Current owner / source family | Lifetime boundary and migration requirement |
| --- | --- |
| `Supervisor`, `CombatInjuryHook`, `LetterPauseHook` | Static supervisor state/epoch/cursor/journal, injury session cache and patch installation state. N01.05 owns process versus load separation and bounded stop/cleanup; N01.06 owns journal delivery/recovery. |
| `BridgeCommon` | Declared tool-parameter cache and SDK journal reflection probe/property cache. N01.02 owns explicit SDK version/binder behavior and unavailable raw arguments; never silently treat failed reflection as validated input. |
| Construction/haul/draft/recovery/mining/drilling/production/Home/wall guards | Patch flags and active callback context coexist with saved game components. N01.04/06 own native disconnect invariants and exception-safe context restoration; inspect every indexed declaration before merging owners. |
| `OrderedWorkHistory` | Runtime order tracking and patch state. N01.05/06 own reuse/reset and authoritative event delivery. |
| `PawnImageCapture`, `RenderDemandDriver`, `VideoStreamDriver`, `PrivatePlayerInput`, `PlayerFrame` | Static instances, frame/UI revision cache, render lease and owned camera/input resources. N01.05 owns OnDestroy/lease expiry/reload cleanup, bounded frame retention and platform adapters. |
| Headless startup | Process-level initialization and game-menu postfix. N01.05 owns idempotence and batch-only presentation suppression. |

Reflection is not limited to Harmony. The index includes `AccessTools`,
`BindingFlags`, GetField/GetMethod/GetProperty and plural forms, invocation,
BridgeCommon reflection helpers, `dynamic` and native-library imports. Named
compatibility adapters remain explicit exceptions requiring boundary tests:

- SDK metadata/raw argument and anonymous-reply reflection: `BridgeCommon`;
  N01.02 owns replacement by validated generated DTO adapters.
- Native private observations: `PawnConfigTool`'s compiled `PawnSettingsRead`
  (work priorities, egg progress and social thoughts), `GearUpkeepTool`,
  `ResourceAcquisitionTool` and spatial reads; N01.03/04 own unavailable-field
  handling and installed-version probes.
- Native dialogs/actions: `DialogTextTool`, `ColonyNamingTool`, `CaravanTool`
  and player UI capture; N01.04/05 own native validation, exact window identity
  and refusal on missing members.
- Harmony method/property resolution and headless `alreadyDisplayed` handling:
  N01.05 owns required-versus-optional health and fault injection.
- `PlayerInputTool`, `VideoStreamTool` and `RenderDemandTool` import libc/X11/
  Xtst or user32 APIs. N01.05 owns platform isolation, native handle cleanup and
  Windows/Linux display/input acceptance. These are not portable pure C# helpers.

Existing native candidates include `scripts/native_interruption_acceptance.py`,
`scripts/native_autosave_acceptance.py`, `scripts/native_player_input_acceptance.py`,
`scripts/native_render_smoke.py` and `scripts/container_native_acceptance.py`;
domain scenarios are mapped by the [test selection guide](../docs/how-to/choose-tests.md).
No native scenario or compiled-artifact verification was run for this documentation
slice. Source anchors/hashes establish the inspected revision only.

## Provenance and distribution audit

The repository's [THIRD_PARTY.md](../THIRD_PARTY.md) already names both origins.
[Headless provenance](../integrations/headless-rim/PROVENANCE.md) pins
`d3c5539ff62c19e76ab8e5d1a268d1dca461e161`, attributes the three C# files/About
metadata and lists modifications. The retained
[license](../integrations/headless-rim/LICENSE) is GPL-3.0. N01 explicitly requires
retained notices, pinned revisions and corresponding source in the unified
distribution. The current headless PowerShell install copies LICENSE but neither
source nor PROVENANCE. That install layout is not evidence that the unified
distribution gate is satisfied. N01.01/07's integrator owns the release manifest
and corresponding-source/notices check, including build scripts and modifications.

[Companion provenance](../integrations/colony-bridge/PROVENANCE.md) pins
`89c2e90fedd51419a3db55a7f9865b0aef29b270`, preserves upstream attribution and
records no license in the reviewed checkout. It expressly does not grant
redistribution rights. A unified assembly cannot assume the companion has acquired
the headless license by being placed beside it. N01.00's integrator must resolve
source/distribution permission and compatibility before release; preserve both
records through any move. This inventory reports repository evidence and the
existing release gate, not a new licensing determination.

The builders reference licensed game and SDK inputs without bundling them.
N01.07 must inspect the actual staged package for accidentally copied dependencies
and applicable third-party notices. A clean source tree or `Private=false` alone
does not prove the contents of an installed directory or cached container image.
