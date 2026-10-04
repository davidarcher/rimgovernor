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

The model interpreter validates its own bounded proposal format before catalog
matching and domain construction; it does not define the shared native boundary.

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
pinned assemblies produce.

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
  cannot check it. Confirmed from the decompiled game, #1794:
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
- Def references are the target's `defName` string; `System.Type` is the type's
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
