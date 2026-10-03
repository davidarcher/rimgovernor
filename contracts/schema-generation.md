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
schemas. Review generation drift for the complete file set.

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
and emits [`proto/defs.proto`](proto/defs.proto): typed messages mirroring
`ThingDef` and `TerrainDef` (the roots), every class their fields reach and
every subclass of a polymorphic field type (all `CompProperties`, verbs, core
and the DLCs). Native fills a message through protobuf reflection by field name,
so field names are the CLR names exactly and each message's `clr_type` option
names its class. The Go and C# bindings are generated from it by the usual
generators. `task defmirror:generate` rewrites `defs.proto`; `task build` runs
`defmirror:build`, which fails when the committed file differs from what the
pinned assemblies produce.
Later def kinds (#1721-#1724) add roots to the same generator.

Inputs. The reference assemblies are the locked NuGet package
Krafs.Rimworld.Ref 1.6.4871 (`ref/net472`): it exposes every def and comp field
and all five DLCs, so the generator runs on Linux and in CI without a game
install, and `defs.proto` is pinned to that version. `--managed-dir <Managed>`
reflects a game install instead (a PC build for a newer game version); the check
then reports drift against the committed file.

Mapping, with no allow-list and no name lists:

- A message holds every public instance field of the class and its bases
  (a redeclared name keeps the most derived).
- Def references are the target's `defName` string; `System.Type` is the type's
  full name string; enums keep their numeric values (flags enums hold the bitmask).
- `Nullable<T>` is a proto3 `optional` field; `List`, `HashSet` and arrays are
  `repeated`; a `Dictionary` is `repeated` `Entry_<Key>_<Value>` messages; a
  collection nested in a collection is a `List_<Element>` message with one
  repeated `items` field. Both synthetic shapes carry the `clr_synthetic` option.
- A field whose class has subclasses is a `<Class>Any` oneof over the class and
  each concrete subclass. `Def.modExtensions` is a repeated empty
  `DefModExtension` message: vanilla defines no subclass; the abstract class
  with no fields is listed in the header.

Def data excludes `[Unsaved]` and non-public fields (stated in the `defs.proto`
header), runtime state and reference cycles; the header lists every field skipped
on the last two grounds:

- Runtime state: a field whose type, collection element or generic argument
  derives from `Verse.Entity` or `UnityEngine.Object` (`Material`, `Texture`, ...),
  implements `Verse.ILoadReferenceable`, is a delegate or is an interface.
- Reference cycle: the type graph is walked depth-first from `ThingDef`, then
  `TerrainDef`, fields in declaration order and subclasses in ordinal order. A
  field whose type (or any subclass of it, since the `<Class>Any` wrapper holds
  them) is still being built closes a cycle and is skipped
  (`GraphicData.attachments`, `PawnRenderNodeProperties.children`). The rule is
  structural and the walk order is fixed, so the drift check is stable; the
  mirror holds no recursive message.

Any other field the tool cannot represent fails generation naming
`Class.field`, writes nothing and exits 1. Fixing it means extending the mapping
rules here, never listing the field.
