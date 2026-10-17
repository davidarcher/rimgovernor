# Compiled building observation checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-buildings <args...>
```

where `<args...>` is the private Bridge DLL, Runtime directory, SDK directory,
licensed game managed directory and Harmony directory, in that order.

The suite loads the actual Contracts wire records and adapter. It checks the
required scope, exact bounded id and definition filters, and that omitted false
facts read back false. No game objects are fabricated or gameplay claimed.

The adapter lists complete matched artificial rows including walls without
aggregation, the player's unless any_faction. Definition filters match exact
native thing or intended construction/install definitions; an empty status list
selects every stage. Native identity, count, footprint, material and
construction correctness still
require root's fresh-game acceptance, including an installation blueprint read
that must not pause the game.
