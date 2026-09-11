# Late journal discovery regression

Run this probe as its own executable. It compiles the actual `BridgeCommon.cs`,
misses journal discovery before any assembly named `RimBridgeServer` is loaded,
then emits that assembly with an internal `RimBridgeCapabilities` type and public
static `Journal` property. The next production `RawArguments` lookup must succeed.
It never resets the private cache. Repeated reads also verify that only the
property is cached, not a journal instance, operation or argument dictionary.

```powershell
dotnet build contracts/tests/native-journal-cache/JournalCacheProbe.csproj -c Release -p:RestoreLockedMode=true -p:RimBridgeSdkDir='C:/path/to/SDK/Assemblies' -p:RimWorldManagedDir='C:/path/to/RimWorldLinux_Data/Managed'
./contracts/tests/native-journal-cache/bin/Release/net472/JournalCacheProbe.exe
```

The actual SDK interface and licensed game references are compilation inputs;
no game is started or installed DLL changed. Do not load the actual
`RimBridgeServer.dll` into this process: the test asserts its absence before
emitting the controlled late-load fixture. This regression proves production
lookup recovery and raw-argument retention, not actual SDK journal initialization
or dispatch. Fresh native acceptance covers those separately.
