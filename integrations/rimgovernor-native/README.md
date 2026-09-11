# RimGovernor native mod

One RimWorld 1.6 package, `davidarcher.rimgovernor.native`, contains the runtime
assembly and RimBridgeServer tools. Harmony and RimBridgeServer load first.
Headless presentation suppression activates only with `-batchmode`.

Build a complete local development package from the repository root:

```powershell
./scripts/build_native_mod.ps1 -RimWorldManagedDir 'C:/path/to/RimWorldWin64_Data/Managed' -HarmonyAssembly 'C:/path/to/0Harmony.dll' -RimBridgeSdkDir 'C:/path/to/RimBridgeServer/1.6/Assemblies' -DotNet 'C:/path/to/dotnet.exe'
```

The command prints the staged `RimGovernor` directory. `-OutputRoot` chooses a
fresh parent directory; an existing directory is refused. Default outputs live
under `.rimgovernor/native-builds`. Add `-Fixture InstallFixture` (or an array of
the supported fixture property names) for test-only tools. Production and fixture
builds use separate source, restore and output directories. The manifest records
the role, enabled fixtures, compiler version and source/dependency/artifact hashes.

Copy the complete staged directory into the game's `Mods` directory while every
RimWorld instance is stopped, then enable RimGovernor after its dependencies.
Fixtures change disposable scenario state; their manifest says `fixture`.
The native entry assemblies are `Assemblies/RimGovernor.Runtime.dll` and
`BridgeTools/RimGovernor/RimGovernor.Bridge.dll`. Source and retained notices are
included under `Source` and `Notices`; external game/SDK/Harmony DLLs are not
bundled. The Bridge bundle also contains the locked Google.Protobuf 3.31.1 runtime
and its four NuGet runtime dependencies, with notices under `Notices/protobuf`.
MSBuild supplies the resolved runtime file list; the builder requires a retained
license for each package/version and records each DLL hash in the manifest.
Dependencies remain beside `RimGovernor.Bridge.dll` for RimBridgeServer's scoped
assembly resolver. Do not move them into the mod's general `Assemblies` directory.

The private build and bundled source include canonical `contracts/proto`, official
C# outputs, and `scripts/generate_protobuf.py` with its pinned tool project/lock.
Native compilation consumes those checked-in official outputs directly; it does
not invoke an experimental JSON generator. Regenerate/check with the included
Python script and pinned .NET tooling, then rebuild using the included native
script and your installed game/SDK/Harmony dependencies. Native restore is locked.
A standalone Mono proof needs the standard netstandard framework facade; installed
game loading and round trips require the fresh native acceptance run.

The implemented [fixed Protobuf methods](../../contracts/native-protobuf-cutover.md)
cover identity, authority, placement previews, basic status/cells and guarded
construction with receipt lookup and progress. Each accepts one `request`
ProtoJSON string and returns one `payload` ProtoJSON string inside the SDK envelope.
Reads do not initialize game components or authority. Native lifecycle hooks
initialize inactive authority; only the trusted host's explicit control path can
acquire it. Model interpretation receives no control or execution capability.

Construction currently implements `PlaceBuilding`. Other operation commands
return Unsupported. Admission checks current identity, generation, lease and
ordinary native placement rules on the game thread. One unsaved per-load ledger
retains up to 4096 attempts without eviction. Exact retries return the original
receipt after revocation; changed requests conflict. Applied records an observed
blueprint, frame or instant building. Pawn completion requires a separate progress
read following the exact native object transitions. Lost transition evidence
remains unknown. New loads start without authority or attempt history.

Status does not issue entity mutation snapshots. Cell reads support terrain,
roof, visibility and traversal, with explicit Unsupported issues for other
requested fields. Pages are bounded single reads; frozen continuation pages and
the remaining observation families are tracked in the backlog.

Run `scripts/native_protobuf_acceptance.py` through
`scripts/container_scenario.py` against a private production package. Its root is
`/worker/run`; add `--rendered` for Xvfb and optionally `--go-preview-smoke` with
the official-wire Linux Go smoke executable. The smoke explicitly transfers its
own scenario's GABS connection and returns it for final native read checks.
The focused C# projects under `contracts/tests/native-proto-*` and
`contracts/tests/native-authority*` test parsing, SDK binding and authority
semantics separately from this game run.

Use `scripts/native_go_service_acceptance.py --root /worker/run --go-service
/inputs/profile/rimgovernor-go` through the scenario launcher with a private
production package to check the Go read-only service. It joins the first GABS
process before starting Go, checks two refreshed HTTP observations, joins Go on
SIGTERM and reconnects without force takeover. A contiguous native operation-event
trace proves the service invoked only identity/status reads. Paused tick, identity
and native generation must remain unchanged across the handoff.

For guarded construction acceptance, build a private package with
`-Fixture GuardedConstructionFixture` and run
`scripts/native_guarded_construction_acceptance.py` through the same launcher.
The fixture selects an existing capable colonist and existing wood, sets ordinary
work priorities and returns legal wall sites. The Go building smoke places one
wall, closes its controller session, and later reopens the same SQLite state to
observe native completion. The scenario advances bounded ticks through
`rimgovernor.native_scenario.advance_game`. Fixture builds are never production
packages; retain the scenario report to distinguish acceptance from compilation.

The headless GPL-3.0 notice and both upstream provenance records remain in Notices.
Companion provenance records the absence of an upstream redistribution license;
this local development package does not grant redistribution rights.
