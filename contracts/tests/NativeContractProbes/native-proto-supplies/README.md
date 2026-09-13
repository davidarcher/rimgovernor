# Compiled supplies observation checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with the
private Bridge DLL, Runtime directory, SDK directory, game managed directory and
Harmony directory:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-supplies <args...>
```

The suite loads the actual adapter, official DTOs and SDK binder/normalizer. It
checks filters, defaults, ownership rules, 64-bit quantities, known-zero versus
unknown held facts and reply bounds. It does not fabricate game objects or
establish gameplay outcomes.

Defaults follow `HomeThingTools`: category haulable, ownership ours, held included.
Supported categories are haulable, food, weapons, buildings and all. Exact native
definition filters replace text search. An inclusive rectangle filters by spawned
root location. Corpses=true restricts to corpse instances; forbiddenOnly filters
forbidden stacks; excludeChunks follows the existing Chunk definition prefix.
Worn apparel/equipment, orbital ships and delivered blueprint/frame materials are
excluded. Nested container contents inherit their physical outer owner's faction
and trader status. A dead pawn's inventory is not usable colony stock.

Ownership ours selects definitions with usable stock; every returned row's units,
stacks and ownership buckets still cover all matching stock of that definition.
Items are complete instances rather than position samples. Holder entries describe
each held stack, not a top-holder ranking. Reserved counts whole usable stacks with
a native reservation, not the reservation's partial quantity. InHomeArea is based
on the spawned root cell. Held=false omits carried/container/trader counts, marks
their issues and leaves holder completeness false. Other quantities then describe
only the explicitly requested spawned scope. No entity CAS snapshots are issued.

Definitions/page, items/holders/corpses per definition are bounded to256. Traversal
is bounded to65536 thing/holder visits and depth16; overflow/refused reads return
unavailable rather than partial totals. Whole replies are limited to1MiB. No frozen
cursor support is claimed. Native acceptance must verify loose/forbidden/fogged,
player/trader-held, corpse and nested-container facts with unchanged ticks.
