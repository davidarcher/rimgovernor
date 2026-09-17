# RimGovernor native mod

One RimWorld 1.6 package, `davidarcher.rimgovernor.native`, contains the runtime
assembly and RimBridgeServer tools. Harmony and RimBridgeServer load first.
Headless presentation suppression activates only with `-batchmode`.

Build a complete local development package from the repository root:

```powershell
./scripts/build_native_mod.ps1 -RimWorldManagedDir 'C:/path/to/RimWorldWin64_Data/Managed' -HarmonyAssembly 'C:/path/to/0Harmony.dll' -RimBridgeSdkDir 'C:/path/to/RimBridgeServer/1.6/Assemblies' -DotNet 'C:/path/to/dotnet.exe'
```

Without an installed game, `./scripts/fetch_native_build_inputs.ps1 -OutputRoot <dir>`
stages pinned substitutes for the three inputs (`<dir>/managed`,
`<dir>/0Harmony.dll`, `<dir>/rimbridge`); CI builds from them.

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
C# outputs, and `scripts/generate_protobuf.go` with its pinned tool project/lock.
Native compilation consumes those checked-in official outputs directly; it does
not invoke an experimental JSON generator. Regenerate/check with the included
Go program (`go run scripts/generate_protobuf.go`) and pinned .NET tooling, then rebuild using the included native
script and your installed game/SDK/Harmony dependencies. Native restore is locked.
A standalone Mono proof needs the standard netstandard framework facade; installed
game loading and round trips require the fresh native acceptance run.

The implemented [fixed Protobuf methods](../../contracts/native-protobuf-cutover.md)
cover identity, authority, clock control, bounded observations, placement previews
and guarded construction/draft/movement/melee operations with receipt lookup and
progress. The method map states each implemented subset. Each accepts one `request`
ProtoJSON string and returns one `payload` ProtoJSON string inside the SDK envelope.
Reads do not initialize game components or authority. Native lifecycle hooks
initialize inactive authority; only the trusted host's explicit control path can
acquire it. Model interpretation receives no control or execution capability.

Operations implement `PlaceBuilding`, temporary `SetDrafted`, exact owned
`MovePawn`, guarded `AttackTarget` and `DesignateThing` restricted to Allow on an
exact visible loose supply item. Other command variants remain unsupported.
Admission checks current identity, generation, lease and
ordinary native placement rules on the game thread. One unsaved per-load ledger
retains up to 4096 attempts without eviction. Exact retries return the original
receipt after revocation; changed requests conflict. Applied records an observed
blueprint, frame or instant building. Pawn completion requires a separate progress
read following the exact native object transitions. Lost transition evidence
remains unknown. New loads start without authority or attempt history.

Movement and combat require an existing canonical owned draft and exact pawn
snapshots. Animals without draft controllers have target snapshots but cannot be
drafted. Movement completion follows the issued job to its exact destination.
Melee completion requires native positive damage from that exact attack to cause
target death or requested standing-target downing. Ordinary direct bullets retain
exact launch/impact lineage; explosive and custom projectile paths remain unavailable.
`ReleaseOwnedDraft` permits exact original-owner cleanup
after Manual or lease expiry without acquiring new authority.

Work-detail pawn reads provide work-only settings snapshots for eligible workers.
`PatchPawn` accepts only bounded work-priority entries, using ordinary native
`SetPriority` and the shared authority/attempt ledger. Snapshots bind the current
work mode, all priorities and disabled work types. External priority changes revoke
authority; owned changes do not. Readback verifies requested priorities, and a
changed or unavailable pawn cannot authorize repeating a settings write.

Supply reads provide Allow snapshots for eligible spawned items; held, fogged,
foreign and unsupported items have no Allow snapshot. Tokens bind colony/load/map,
exact identity, definition, position, quantity, forbidden state and faction.
Allow uses the ordinary Unforbid designator under the shared authority and attempt
ledger. Separate progress reads require the exact item still allowed; disappearance
remains unknown and re-forbidding reports an unsuccessful outcome. This boundary
does not identify the original supply cohort or authorize automatic startup work;
Go method/store/worker composition remains gated in G01.05.

Status does not issue entity mutation snapshots. Cell reads support terrain,
roof, visibility and traversal, with explicit Unsupported issues for other
requested fields. Pages are bounded single reads; frozen continuation pages and
the remaining observation families are tracked in the backlog.

Building reads return complete bounded rows for exact buildings, blueprints and
frames, including walls, occupied cells, material, hit points and construction
work/resources. Detailed settings, bills, CAS snapshots and network enumeration
remain explicitly unavailable. Reads never aggregate away individual walls.

Colony-facts reads provide native counts, accessible stock, sleeping capacity,
temperature and storage. Optional planning returns a visible 45-by-45 region
around the colony anchor and requested native definitions/costs. Occupancy is an
explicit native edifice/blueprint/frame fact. The human food-supply section shares
its typed native collector with compatibility JSON, preserving inventory ownership,
eligible eaters, nutrition and rot deadlines. Its completeness counts consumer and
stock rows together; food item references carry identity/definition only.
Raw runway remains separate from diet/rot forecasts. Forecast inputs include the
combined human/animal food census, crop work and patient quantities from the same
native compatibility collector. Optional unknown quantities remain absent; crop
yield is not a promise of available food. Forecast completeness counts the combined
supply section once plus animal, crop and patient rows. Upkeep, development and
other unported sections remain explicitly unavailable.
Oversized collections/replies return unavailable.

The `scripts/native_protobuf_acceptance.py`/`container_scenario.py`,
`native_go_service_acceptance.py` and `native_guarded_construction_acceptance.py`
scenario scripts this section once described (protobuf wire acceptance against a
private production package, a Go read-only service handoff check, and a guarded
construction cancellation check) were removed with the rest of the Python
acceptance toolchain in
[G01.13](https://github.com/davidarcher/rimgovernor/issues/33); equivalent Go
harnesses are tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).

The focused `native-proto-*` and `native-authority*` probes in the consolidated
`contracts/tests/NativeContractProbes.csproj` (run with `dotnet run --project
contracts/tests/NativeContractProbes.csproj -- <probe-name>`) still test parsing, SDK
binding and authority semantics independent of a live game run.

The headless GPL-3.0 notice and both upstream provenance records remain in Notices.
Companion provenance records the absence of an upstream redistribution license;
this local development package does not grant redistribution rights.
