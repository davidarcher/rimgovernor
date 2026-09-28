# Pawn snapshot state

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged net472 project, then run it with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-pawn-control-state <args...>
```

where `<args...>` is the private Bridge DLL, Runtime directory, SDK directory,
game managed directory and Harmony directory. The tests exercise the compiled
record implementation using uninitialized native identity references; they do
not create game state or install native hooks. Private package compilation
checks the actual game/Harmony/SDK APIs separately.

`NativePawnControlState.Initialize()` must run explicitly during capability
registration or lifecycle initialization. Typed observations only verify live exact
hook membership; they never install hooks or allocate native authority. There is
no support advertisement in this foundation slice.

`Observe`/`Check` produce stable opaque native snapshot tokens for exact pawn and
colony/load/map identity, context transitions, draft setter and ordered-job epochs,
position, faction, native draft eligibility, current job and queued job targets.
At most4096 pawn records per unsaved Game and256 queued jobs/target entries are
supported; overflow is explicit. This is a draft-control CAS, not a health/settings
snapshot or permission to issue arbitrary orders.

Drafts are plan-owned (#939): native keeps no draft claim or release ticket.
The draft setter hook (`DraftOwnership`) only advances the per-pawn draft
revision the snapshot token covers; the controller's undraft sweep undrafts
pawns no live plan needs.
