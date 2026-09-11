# Compiled building observation checks

Build `NativeProtoBuildings.csproj` with `-p:RestoreLockedMode=true`. Run its net472
executable with the private Bridge DLL, Runtime directory, SDK directory, licensed
game managed directory and Harmony directory, in that order.

The suite loads the actual generated DTOs, adapter, SDK binder and SDK response
normalizer. It checks required scope, exact bounded filters, unsupported detail,
finite fractions, explicit false facts, unavailable snapshot/network distinctions,
and whole-reply size refusal. No game objects are fabricated or gameplay claimed.

The adapter lists complete matched rows including walls without aggregation.
Defaults are player-only and artificial. Definition filters match exact native
thing or intended construction/install definitions; regions select anchors using
inclusive coordinates. Empty filters select all. Pending includes blueprints and
frames. Damage filters select only HP-bearing things strictly below the fraction.
Collections over the requested page limit return unavailable; no frozen paging is
implemented. Power network completeness is false with unknown counts. Row issues
identify unsupported settings, services, bills, thermal sides and CAS snapshots.
Native identity, count, footprint, material and construction correctness still
require root's fresh-game acceptance, including an installation blueprint read
that must not pause the game.
