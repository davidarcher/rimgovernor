# Room observation checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project and run it with the
private Bridge DLL, Runtime directory, SDK directory, game managed directory and
Harmony directory:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-rooms <args...>
```

It loads the actual adapter, official messages and SDK binder.

ListRooms defaults exclude psychologically outdoor rooms and doorways, matching
the existing room census. IDs identify the current room graph within the returned
context and can change after construction; they are not CAS tokens. Exact room IDs
and inclusive rectangle filters intersect. Rectangle selection tests actual room
cells, not merely its bounding rectangle. Rows retain whole-room facts.

Contents are complete building counts, optionally including boundary buildings;
loose stock belongs ListSupplies. Zone IDs come from the authoritative zone grid.
Role/stat reads may fill native room caches, but do not change room geometry or
roof areas. Stats cover native Cleanliness, Wealth, Space, Beauty and Impressiveness.
Missing temperature/stat facts stay explicitly unavailable. Geometry,
counts, room membership and contents failures refuse the census. Exact cell lists
are optional and limited to4096 cells per room. Room/pawn/thing scans are bounded,
results and child collections to256, replies to1MiB. No frozen cursor is issued.

The census refreshes native dirty regions first. Native room entries with no regions
are filtered only when both their cell count and cell enumeration prove they are
empty; inconsistent geometry remains unavailable. Compiled regression checks use
native room/district objects to cover retained empty entries and conflicting counts.

Native acceptance compares naturally generated indoor rooms with the native
census, checks cells/contents/boundary/filters and unchanged paused context. It
must not claim populated bed, pawn or stockpile membership if those are absent.
