# Native ProtoJSON boundary probe

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. This net472 executable compiles the production
`ProtoBoundary` and `NativeControlAuthority` with explicit journal/game access
seams. It verifies malformed official ProtoJSON, Unicode and byte limits, exact
raw/bound argument agreement, payload dictionary serialization, and nonallocating
identity reads. It additionally invokes `BindArguments` from the supplied actual
RimBridgeServer assembly to check that raw object parameters preserve strings
and nonstrings.

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-boundary 'C:/path/to/RimBridgeServer.dll'
```

Use the installed SDK assembly directory intact so its dependencies can resolve.
No game is launched and no installed assemblies are copied or changed. The game
and raw-journal seams are deliberately small and do not prove real operation
journal capture, game-thread scheduling, or native SDK response dispatch. Those
remain in fresh native package acceptance. Parser acceptance is syntax only;
negative map IDs/null facts are intentionally accepted by the official parser
and must be refused by each method's semantic validator.

The identity check leaves native generation absent when no authority owner
exists. Looking up existing authority never initializes a clock; reading its
status can still apply lease-expiry/context invalidation as intended by that
owner. It does not create game components or issue orders.

Note: without a real `RimBridgeServer.dll` path supplied as an argument, the
probe throws before reaching its SDK binder checks (`ArgumentException: Supply
installed RimBridgeServer.dll for real SDK binder checks`) — this matches its
pre-consolidation behavior exactly and is not itself a regression.
