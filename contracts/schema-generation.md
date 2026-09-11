# Generated wire contracts

G01 owns the canonical [schemas](schemas/) and [output manifest](generation.json).
The repository-owned Go generator uses the pinned toolchain in `go/.go-version`.
Its source is versioned with the repository; generated headers identify the schema
SHA-256 and generator format version. Schema files use LF so hashes agree across
Windows and Linux. Native and controller consumers share this source of truth.

## Supported subset

The generator covers placement requests and current preview replies. It accepts schema
metadata (`$schema`, `$id`, `title`, `description`), local `$defs`/`$ref`, named
objects with explicit `required` and `additionalProperties: false`, bounded arrays,
strings, booleans and bounded integers. Definition names supply generated type
names. References are local and acyclic. Unsupported keywords and inconsistent
constraints are generation errors. String enums, boolean constants and nullable
int32 values are supported. Closed `oneOf` variants reference two named objects
with distinct required boolean `success` constants. C# optional nullable fields
remain unsupported; required nullable fields distinguish missing data from null.

Three explicit extensions preserve native request constraints:

| Keyword | Contract |
| --- | --- |
| `x-maxUTF16Length` | Maximum decoded string length in UTF-16 code units; supplementary characters count twice. |
| `x-nonBlankDotNet: true` | Reject empty strings and strings containing only the native .NET whitespace characters. |
| `x-integerToken: true` | Require an integer JSON token spelling; `1.0` and `1e0` are refused even when mathematically integral. Bounds remain explicit. |

Generated decoders reject duplicate decoded property names, missing required
fields, null where unsupported, unknown fields, overflow and malformed JSON before
returning typed values. Strings must contain Unicode scalar values: invalid UTF-8
and unpaired surrogate escapes are refused. This avoids replacement-character
collisions between serializers. No trimming, number coercion, case folding or
identifier normalization occurs at this boundary.

These rules intentionally tighten Newtonsoft's permissive parser and SDK binder.
Existing parser observations are reference material, not compatibility gates.
The placement `rotation` remains a string: supported names and ordinary placement
refusals belong to native semantic validation.

## Generation and acceptance

The manifest lists every schema and output explicitly. Generation must reject
invalid schemas and output collisions before publishing files. Check mode compares
all declared outputs against deterministic regeneration without modifying them.
Generated models are boundary values, not validated evidence of pawn work.

G01.02a supplies Go, C# and Python generation with shared boundary cases.
G01.02b–d add current replies and native consumer wiring. New Go sessions start
with fresh state; no legacy-state or historical-wire parity gate applies. TypeScript generation is added when an
actual dashboard consumer needs a migrated surface.

Run from `go/`:

```powershell
go run ./cmd/contractgen -root .. contracts/generation.json
go run ./cmd/contractgen -root .. -check contracts/generation.json
```

Review schema and generated diffs together. Check mode must fail on an altered,
missing or stale declared output. Keep compiler/build products outside generated
source directories, under ignored task-specific `.rimgovernor/` paths.

Run `python scripts/check_placement_requests.py --check` to validate the generated
Python boundary. Go tests run the same request cases. The standalone C# project
`contracts/tests/csharp/PlacementRequests.csproj` requires .NET SDK 8.0.424 and
locked NuGet dependencies; CI compiles net472/net8 and executes the shared cases.
Run `python scripts/check_placement_responses.py` for current reply cases.
The C# harness accepts the reply fixture as its second argument. These checks
establish typed boundaries; native preview effects require separate game acceptance.
