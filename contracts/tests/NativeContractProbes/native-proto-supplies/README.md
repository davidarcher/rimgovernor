# Compiled supplies observation checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with the
private Bridge DLL, Runtime directory, SDK directory, game managed directory and
Harmony directory:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-supplies <args...>
```

The suite loads the actual adapter, and the Contracts wire records. It
checks filters, defaults, ownership rules, 64-bit quantities and complete stock
projection. Unspawned game-object fixtures exercise classification and projection;
they do not establish gameplay outcomes or live traversal correctness.

Defaults follow `HomeThingTools`: category haulable, ownership ours, held included.
Supported categories are haulable, food, weapons, buildings, packed objects and all.
Packed objects use the native MinifiedThing class and subclasses, across container
definition names; the compiled probe checks unfamiliar names and rejects an ordinary
thing whose definition happens to be named MinifiedThing. Exact native
definition filters replace text search. An inclusive rectangle filters by spawned
root location. Corpses=true restricts to corpse instances; forbiddenOnly filters
forbidden stacks; excludeChunks follows the existing Chunk definition prefix.
Worn apparel/equipment, orbital ships and delivered blueprint/frame materials are
excluded. Nested container contents inherit their physical outer owner's faction
and trader status. A dead pawn's inventory is not usable colony stock.

Ownership ours selects definitions with usable stock; every returned row's units,
stacks and ownership buckets still cover all matching stock of that definition.
Items are complete instances rather than position samples. Holder entries describe
each held stack, not a top-holder ranking. Held=false scopes the read to spawned
objects. Loose items carry native supply CAS snapshots when eligible; held items
carry holder-bound snapshots.

An exact definition filter accepts at most 256 names. Results retain every matching
definition and stack, including the probe's 300-stack fixture. Holder nesting past
16 levels fails the read. No frozen cursor support is claimed. Native acceptance must verify loose/forbidden/fogged,
player/trader-held, corpse and nested-container facts with unchanged ticks.
