# Guarded draft caller checks

Build `NativeDraftOperations.csproj` with .NET SDK, then run the resulting
`net472/NativeDraftOperations.exe` with the private compiled bridge DLL followed
by dependency directories: game managed assemblies, RimBridgeServer SDK,
Harmony, and the private runtime assembly directory if separate from the bridge.

The executable loads the actual compiled bridge and generated Protobuf types.
It checks explicit field presence, exact snapshot and owner guards, refusal to
adopt player drafts, shared attempt capacity and replay, cleanup projections,
unverified progress, and the SDK raw ProtoJSON argument binder. It does not
install hooks or prove gameplay outcomes. Run the pawn control state, authority
hook, and operation envelope neighboring checks, then isolated native game
acceptance after registration and pawn observation wiring are integrated.
