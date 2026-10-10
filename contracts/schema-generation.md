# Generated wire contracts

The shared native boundary is defined in [Protocol Buffers](proto/README.md).
N01 owns the canonical package; Go reviews its consumer coverage. Complete the
shared package before expanding native or Go adapters. Source/consumer coverage
and semantic constraints live beside the `.proto` files.

Use official generators and runtimes:

| Language | Compiler/plugin | Runtime | Generated source |
| --- | --- | --- | --- |
| C# net472 | protoc30.0 from Grpc.Tools2.72.0 | Google.Protobuf3.31.1 | `generated/protobuf/csharp` |
| Go | protoc30.0 and protoc-gen-go1.36.11 | google.golang.org/protobuf1.36.11 | `generated/protobuf/go` |

The repository scripts invoke official tools, restore pinned packages, compare
generated output and run proofs. They do not interpret schema syntax or emit
language source; the one exception is the [def mirror](#def-mirror), which emits
`.proto`, never Go or C#. See [C# commands](../tools/protobuf/README.md) and
[Go commands](../tools/protobuf/go/README.md). Keep private build outputs in fresh
ignored `.rimgovernor/` directories; commit official generated bindings with their
schemas, except `generated/protobuf/csharp/Defs.cs` (57 MB): it is gitignored and
generated from `defs.proto` by `generatecsharp` (the pinned Grpc.Tools protoc) at the
start of `scripts/build_native_ref.sh` and `scripts/build_native_mod.ps1`; run
`go -C go run ./internal/protobufgen/cmd/generatecsharp` to produce it for an IDE.
`--check` does not compare it; `defs.proto` itself is checked by `defmirror:build`.
The Go binding `defspb/defs.pb.go` stays committed (generating it takes minutes). Review generation drift for the complete file set.

Use official ProtoJSON for the MCP payload string. Each advertised method has a
fixed generated request/reply type; SDK reflection must not serialize generated
CLR properties. No generic tool-name/argument bag enters the authoritative
protocol. Service descriptors describe the method boundary without requiring a
new gRPC server.

Protobuf enforces field types and exclusive oneof representation. Ordinary
boundary validators additionally require field presence, supported enum cases,
valid IDs, collection bounds and native invariants. Optional scalars distinguish
unknown from zero/false. Complete empty collections differ from unavailable or
partial scans. Receipt admission and later observed pawn outcomes are separate.
Do not reproduce historical numeric spelling, UTF-16 parser limits or old-save
wire behavior; current game state is disposable.

Cross-language proofs retain independent C# and Go origins, parse both binary and
ProtoJSON with official runtimes, and re-emit separate echoes. Their scope is
serialization, generated compilation and platform runtime support. Fresh game
acceptance remains necessary when adapters are integrated.

## Def mirror

`tools/defmirror` (C#, locked restore) reflects the game's managed assemblies
and emits [`proto/defs.proto`](proto/defs.proto): typed messages mirroring every
concrete `Verse.Def` class (the roots; abstract classes are reached through
their subclasses), every class their fields reach and every subclass of a
polymorphic field type (all `CompProperties`, verbs, quest and think nodes, core
and the DLCs). `DefSets` is the message of one repeated field per root class
but `ThingDef` and `TerrainDef`, which the catalog carries itself. Native fills
a message through protobuf reflection by field name,
so field names are the CLR names exactly and each message's `clr_type` option
names its class. The Go and C# bindings are generated from it by the usual
generators. `task defmirror:generate` rewrites `defs.proto`; `task build` runs
`defmirror:build`, which fails when the committed file differs from what the
pinned assemblies produce. `defmirror --report <file>` instead lists the game's
constants, static curves and `[Unsaved]` data fields for `go run ./cmd/catalogaudit`
(from `go/`), which also diffs the recording's def rows against the game's XML
defs and counts the skipped fields by reason.

Inputs. The reference assemblies are the locked NuGet package
Krafs.Rimworld.Ref 1.6.4871 (`ref/net472`): it exposes every def and comp field
and all five DLCs, so the generator runs on Linux and in CI without a game
install, and `defs.proto` is pinned to that version. `--managed-dir <Managed>`
reflects a game install instead (a PC build for a newer game version); the check
then reports drift against the committed file.

Mapping, with no allow-list and no name lists:

- A message holds every instance field of the class and its bases, whatever
  its visibility (a redeclared name keeps the most derived). The game's XML
  loader looks a field up by name on the class and its bases and fills it
  whether it is public or private, so a private field the loader fills
  (`ThingDef.verbs`, `SimpleCurve.points`) is def data; the only exclusions are
  `[Unsaved]` and runtime state, below. (Stated from the loader's behaviour as
  known; the reference assemblies carry no method bodies, so the generator
  cannot check it. Confirmed from the decompiled game:
  `XmlToObjectUtils.SearchTypeHierarchy` uses `GetField(name, Instance | Public
  | NonPublic)` up the base chain, and `DirectXmlToObject.ObjectFromXml`
  builds with `Activator.CreateInstance(type)`, so a class with no public
  parameterless constructor cannot be loaded.)
- Every `Verse.Def` message ends with the derived field `modPackageId`, an
  `optional string` whose `clr_path` field option names the member path native
  reads from the mirrored object, `modContentPack.PackageId`:
  `Def.modContentPack` is `[Unsaved]`, so reflection alone cannot reach it. It is
  unset for a def with no mod. `clr_path` is the one mechanism for a derived
  field: a field with the option is not a CLR field and is read by path.
- Def references are the target's `defName` string; a field of one (single or
  repeated) carries the `clr_def_ref` option naming the referenced Def class, which
  the dangling-reference gate in `bridge` reads (Dictionary keys and values and
  nested collections carry none); `System.Type` is the type's
  full name string; enums keep their numeric values (flags enums hold the bitmask).
- `RimWorld.QuestGen.SlateRef<T>` (every quest node parameter) is the string it
  holds, the field's XML text: a literal or a `$variable`. Native reads the
  struct's one private string, `slateRef`.
- `Nullable<T>` is a proto3 `optional` field; `List`, `HashSet` and arrays are
  `repeated`; a `Dictionary` is `repeated` `Entry_<Key>_<Value>` messages; a
  collection nested in a collection is a `List_<Element>` message with one
  repeated `items` field. A repeated element that is a reference type mirrored as a
  message (a class, a `<Class>Any` wrapper, a nested collection) is wrapped in an
  `Opt_<Element>` message with one message-typed `value` field, unset for a null
  element: the game uses null list entries positionally (`ThoughtDef.stages`), so
  skipping or defaulting one would shift the states. Structs, enums and
  string-mapped elements (def references, `System.Type`) are not wrapped, and a
  null one fails the read. A `Dictionary` value is a message field of its entry,
  unset for a null message value. The three synthetic shapes carry the
  `clr_synthetic` option (`entry`, `list`, `optional`).
- A field whose class has subclasses is a `<Class>Any` oneof over the class and
  each concrete subclass. `Def.modExtensions` is a repeated empty
  `DefModExtension` message: vanilla defines no subclass; the abstract class
  with no fields is listed in the header.

Def data excludes `[Unsaved]` fields and runtime state; the header lists every
field skipped as runtime state:

- A field whose type, collection element or generic argument derives from
  `Verse.Entity` or `UnityEngine.Object` (`Material`, `Texture`, ...), implements
  `Verse.ILoadReferenceable` (unless it is a class of the generator's `DataClasses`
  table, which XML builds: `RitualRole`, the slots of `RitualBehaviorDef.roles`),
  is a delegate, is an interface, is `System.Object`
  (untyped: no message can hold it, `ThingSetMakerParams.custom`) or is any other
  class outside the game's def assemblies (BCL, Unity and Steam classes such as
  `MaterialPropertyBlock`: def data never uses one).
- A non-public field whose generic type the mapping has no shape for
  (`DefMap<,>`, `Stack<>`, `NativeArray<>`, `Pair<,>`, ...): a cache or scratch
  buffer, which XML never fills with data. The same type in a public field
  fails generation (below).
- A class the XML loader cannot build: a concrete class with no public
  parameterless constructor (a worker or handler constructed with its def or
  map: `TileMutatorWorker`, `SubEffecter`, `PowerNet`), and a base class none
  of whose concrete subclasses has one. An abstract class nothing concrete
  extends stays (`ResearchMod`, `DefModExtension`: a mod's extension point).
- A non-public field holding one instance of a class family (abstract, or with
  subclasses) that no public field reaches: the lazily built worker, graphic
  or noise module (`workerInt`, `graphicInt`, `BiomeDef.worker`). The data
  a non-public field holds is a collection or a struct
  (`SimpleCurve.points`, `RulePack.rulesRaw`, `ThingFilter.allowedQualities`,
  `BiomeDef.wildAnimals`, `Scenario.parts`, `PrefabDef.things`) or a class a
  public field also reaches, and those stay. Reference assemblies carry no
  method bodies, so this is a rule over the type graph, not a read of the
  loader.
- The classes of the generator's `RuntimeClasses` table (and their
  subclasses): the few no rule above separates, each with a one-line reason,
  printed in the `defs.proto` header. Adding an entry needs the same evidence
  as the existing ones; a class not shown to be runtime stays mirrored.

A field of an excluded class is skipped like any other runtime state, so a
class only such fields reach leaves the schema with them.

Reference cycles are not excluded: the type graph is walked depth-first from
`ThingDef`, then `TerrainDef`, then every other concrete `Verse.Def` class in
ordinal order of full name, fields in declaration order and subclasses in
ordinal order, and a field that reaches a class already begun is a recursive
message (`QuestNode` children, `ThinkNode.subNodes`, `ThingSetMaker` options,
`GraphicData.attachments`, `PawnRenderNodeProperties.children`). The walk order
is fixed, so the drift check is stable. A tree def (a quest script, a think
tree) carries its whole tree. Native fills by object graph, so an object that
reaches itself again (a runtime back pointer no [Unsaved] marks) fails the read
naming `Class.field` instead of recursing.

An enum whose underlying type is `uint` and has a member above `int32` max (a
flags `All`) is carried as the two's-complement `int32` of its value, because
proto enums are `int32`; `PsychicRitualRoleDef.Condition.All` is `-1`.

Any other field the tool cannot represent fails generation naming
`Class.field`, writes nothing and exits 1. Fixing it means extending the mapping
rules here, never listing the field.

### Game constants

Beside the def messages, `defs.proto` carries the game's static members, typed
per class (no flat keyed table), so a renamed or mistyped constant breaks the
Go build instead of reading zero. For every class or struct of the namespaces
`RimWorld`, `Verse`, `Verse.AI`, `Verse.AI.Group`, `RimWorld.Planet`,
`RimWorld.QuestGen` and `RimWorld.BaseGen` (the generator's `ConstantNamespaces`;
widening is an edit there) that holds a carried member, one `<Class>Constants`
message carries the `clr_type` of the class and one field per member, named by
the CLR name, in declaration order. The root `GameConstants` message has one
field per such class (named by the class, namespace-prefixed only when two share
a simple name). The messages hold no values: native fills them by reflection.

- Carried: every `const` and `static readonly`, whatever its visibility, of a
  primitive, `string` or enum type (a `char` is its `uint32` code unit) or of a
  struct the def-field mapping represents (`IntRange`, `FloatRange`, `IntVec2`,
  `IntVec3`, `CellRect`, `LevelThresholds`, `PathFinderCostTuning`, `Color`, ...),
  mapped exactly as a def field of that type; or `Verse.SimpleCurve`, the
  existing `SimpleCurve` message (its points, unset for a null curve; the
  generator evaluates nothing); or a one-dimensional array or `List<T>` of
  primitives, strings or enums, a `repeated` field. A `private` array or `List`
  is not carried: the game keeps buffers and caches there and fills them while it
  runs (`LanguageWorker_Czech._replaceRegexKeys` starts as 107 nulls), so the value
  depends on what was read before, not on the build. The header lists each with
  that reason.
- Fill and access: `DefMirrorFill.BuildStatics` walks the root's fields, finds each
  class by its `clr_type` in the loaded assemblies (not the reference assemblies: a
  public `const` is inlined from those) and reads the static field of each member's
  name, a `const` with `GetRawConstantValue` and a `static readonly` from the live
  field. A class or member the game no longer has fails the read naming
  `Class.field`. The same `game_constants` message rides on `DefinitionCatalog` and
  `CreationDefinitionCatalog` (static data of the build, readable at the main menu);
  Go reads it with `DefinitionCatalog.GameConstants()`, which errors on a catalog
  without it. A `static readonly` value is fixed at the class initializer, so it
  can depend on world state only through what that initializer reads; the two
  recordings of the same game in #2628 carried identical `game_constants`.
- Every enum of the namespaces is emitted, reachable from a def field or not
  (`TileMutatorWorker_Stockpile_StockpileType`).
- Excluded by rule: `Dialog_*` classes, `Widgets`, `DevGUI`, `*DefOf` classes,
  other namespaces (`LudeonTK`, `Ionic.Zlib`, ...) and compiler-generated types
  and members.
- Not carried yet: members of any other type (`HashSet`, `Dictionary`,
  arrays and lists of structs or classes such as `List<IntVec3>`, multidimensional
  arrays, `LudeonTK.ComplexCurve`, `Texture2D`, `System.Type`, ...). The
  `defs.proto` header lists each with its type and reason; there is no name list.
  A public member of a carried type the mapping cannot represent fails
  generation naming `Class.member`, as a def field does.

#### Game facts kept outside the mirror

A game fact is a typed mirror read, derived in Go from mirrored rows, or an
explicit error. These stay outside `GameConstants` and the def rows, each for a
reason a reflection walk cannot remove (sizes and timings:
[mirror measurements](../docs/developers/architecture/mirror-measurements.md);
RimGovernor's own caps: [kept constants](../docs/developers/contracts/kept-constants.md)):

| Fact | Why it stays outside | Owner |
|---|---|---|
| `GenTemperature.RotRateAtTemperature` full-rate temperature (`FullRotRateC`) | The rot curve is literals inside a method body, not a field. Native evaluates the function and bisects for the first temperature whose rate reaches 1; it fails naming the function if none does. | `NativeDefinitionCatalogTool.FullRotRateC`; carrier decided in #2629 |
| `ThingDefOf.Silver` (currency) and `ThingDefOf.Wort` def names | `*DefOf` classes are excluded by rule: their fields are def references resolved after load, not constants. The game's own code names them (`Tradeable.IsCurrency`, `Building_FermentingBarrel`). | native, #2629 |
| Any literal inside a method body (a magic number in `StatWorker`, `JobDriver`, `Pawn_*` code) | Reflection sees fields, not method bodies. These are code logic, not constants: the audit family of #2635 lists each worker class as owned in Go or unowned, and a lookup of an unmirrored fact errors naming the class. | #2635 and the stat children (#2636 to #2639) |
| Members of unsupported shapes: `HashSet`, `Dictionary`, arrays and lists of structs or classes, multidimensional arrays, `LudeonTK.ComplexCurve`, `Texture2D`, `System.Type`; 12 static curves that are not `readonly` | The mapping cannot represent them. `defs.proto` lists each with its reason; a public one of a carried type fails generation instead of being skipped. | generator header |
| `private` arrays and `List`s | Runtime buffers and caches, filled while the game runs (`LanguageWorker_Czech._replaceRegexKeys`). | generator rule |
| Classes outside `ConstantNamespaces`, `Dialog_*`, `Widgets`, `DevGUI`, `*DefOf` | UI, editor and def-reference classes. Widening the namespaces is one generator edit, weighed against the generated-code growth in the measurements. | generator rule |
| Per-world and per-pawn state (a pawn's skills, a map's temperature, `[Unsaved]` runtime fields) | State, not static data; it travels in observation frames. About 129 `[Unsaved]` data fields are caches derivable from rows ([static-data census](../docs/developers/static-data-census.md)). | observation protocol |

### Class chains

The base-class chain of a class is not a field of each row: a family (any
no-sunlight game condition, a mod's subclass included) is a base class, and a
`System.Type` value in a row (`conditionClass`, a worker class) names a class
that is no def. `DefinitionCatalog.class_chains` (`ClassChain`: CLR full name,
base classes nearest first up to but excluding `System.Object`, sorted by name)
holds the chain of every class the fill touched: the class of each mirrored
object (each def's own class among them) and each `System.Type` value written,
so a mod's classes are covered. Go matches with
`DefinitionCatalog.ClassIsA(class, base)` and `RowIsA(row, base)`
(`bridge/catalog.go`): no class list, and a class the table lacks is an error.
