# Guarded draft caller checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-draft-operations <args...>
```

where `<args...>` is the private compiled bridge DLL path followed by dependency
directories: game managed assemblies, RimBridgeServer SDK, Harmony, and the
private runtime assembly directory if separate from the bridge.

The executable loads the actual compiled bridge and generated Protobuf types.
It checks explicit field presence, exact snapshot and owner guards, refusal to
adopt player drafts, shared attempt capacity and replay, cleanup projections,
unverified progress, and the SDK raw ProtoJSON argument binder. It does not
install hooks or prove gameplay outcomes. Run the pawn control state, authority
hook, and operation envelope neighboring checks (now other probe names within
the same `NativeContractProbes.csproj`), then isolated native game acceptance
after registration and pawn observation wiring are integrated.
