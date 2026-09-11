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
The two DLLs are `Assemblies/RimGovernor.Runtime.dll` and
`BridgeTools/RimGovernor/RimGovernor.Bridge.dll`. Source and retained notices are
included under `Source` and `Notices`; external game/SDK/Harmony DLLs are not
bundled. Rebuild sources using the included script and your installed dependencies.

The headless GPL-3.0 notice and both upstream provenance records remain in Notices.
Companion provenance records the absence of an upstream redistribution license;
this local development package does not grant redistribution rights.
