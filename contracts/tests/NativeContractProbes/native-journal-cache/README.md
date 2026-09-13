# Late journal discovery regression

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. It compiles the actual `BridgeCommon.cs`,
misses journal discovery before any assembly named `RimBridgeServer` is loaded,
then emits that assembly with an internal `RimBridgeCapabilities` type and public
static `Journal` property. The next production `RawArguments` lookup must succeed.
It never resets the private cache. Repeated reads also verify that only the
property is cached, not a journal instance, operation or argument dictionary.

```powershell
dotnet build contracts/tests/NativeContractProbes.csproj -c Release -p:RestoreLockedMode=true -p:RimBridgeSdkDir='C:/path/to/SDK/Assemblies' -p:RimWorldManagedDir='C:/path/to/RimWorldLinux_Data/Managed'
dotnet run --project contracts/tests/NativeContractProbes.csproj --no-build -c Release -p:RimBridgeSdkDir='C:/path/to/SDK/Assemblies' -p:RimWorldManagedDir='C:/path/to/RimWorldLinux_Data/Managed' -- native-journal-cache
```

The actual SDK interface and licensed game references are compilation inputs;
no game is started or installed DLL changed. Do not load the actual
`RimBridgeServer.dll` into this process: the test asserts its absence before
emitting the controlled late-load fixture. This regression proves production
lookup recovery and raw-argument retention, not actual SDK journal initialization
or dispatch. Fresh native acceptance covers those separately.

Note: this probe compiles the REAL `BridgeCommon.cs` and REAL
`RimBridgeServer.Sdk.IRimBridgeContext` against a licensed SDK, unlike the other
in-process probes in this project which use a fake `IRimBridgeContext`. Its
Compile/Reference items are gated behind `RimBridgeSdkDir`/`RimWorldManagedDir`
specifically to avoid a shape conflict with that always-on fake; supplying those
properties alongside the default probes remains an open follow-up.
