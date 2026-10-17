# Native wire boundary probe

This probe is part of the consolidated `NativeContractProbes.csproj` and runs in
the gate. It compiles the production `ProtoBoundary` and `NativeControlAuthority`
against the fake Verse stub and checks identifier limits (surrogates, NUL, the
128-byte UTF-8 bound) and nonallocating identity reads.

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-boundary
```

The identity check leaves native generation absent when no authority owner
exists. Looking up existing authority never initializes a clock; reading its
status can still apply context invalidation as intended by that owner. It does
not create game components or issue orders. Game-thread scheduling and the
`/native/{op}` route are proved by native acceptance.
