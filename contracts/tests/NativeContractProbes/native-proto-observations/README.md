# Compiled observation boundary checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged net472 project and run it with the
private Bridge DLL, Runtime directory, SDK directory, licensed game managed
directory and Harmony directory, in that order (same layout as
native-proto-placement):

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-observations <args...>
```

The suite loads actual compiled adapters and uses the actual SDK binder. It checks
presence, bounded unique geometry, overflow-safe rectangles, unsupported fields,
finite radius, and the Go singleton map-dimension request with all fields false.
It does not create game state or claim native pawn/threat/cell outcomes.

This slice supports status summary and optional need/hediff detail. Unrequested
sections have explicit issues; missing trackers are unavailable, not healthy zero.
Cell reads support terrain, roof, visibility and traversal only; other requested
fields fail Unsupported. Exact selection order is retained; rectangles use z/x
order and report their inclusive bounding rectangle. Frozen cursors remain unsupported.
Pawn rows expose available draft-control CAS and claims; these request/binder checks
do not establish native hook behavior. Collection/reply overflow cannot truncate success.
Root integration owns capability advertisement and fresh-game acceptance.

N01.03: the shared stateless CAS/cursor helper (`NativeObservationSnapshot`) is
exercised directly -- cursor round-trip, fail-closed on a changed seed or identity,
malformed input refused rather than thrown, and snapshot token determinism/
per-entity distinctness. Pawn/room/research/building `Validate` now accept a
nonempty cursor up to 4096 bytes (previously any nonempty cursor was refused) and
still refuse an oversized one. These are compiled boundary checks only; whether a
resumed page actually returns the next rows against real map/pawn/research state is
native acceptance and is not established here.
