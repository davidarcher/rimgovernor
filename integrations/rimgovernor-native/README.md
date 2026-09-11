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

The headless GPL-3.0 notice and both upstream provenance records remain in Notices.
Companion provenance records the absence of an upstream redistribution license;
this local development package does not grant redistribution rights.
