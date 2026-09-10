# Generated wire contracts

G01 owns the canonical [schemas](schemas/) and [output manifest](generation.json).
The repository-owned Go generator uses the pinned toolchain in `go/.go-version`.
Its source is versioned with the repository; generated headers identify the schema
SHA-256 and generator format version. Schema files use LF so hashes agree across
Windows and Linux. Native and controller consumers share this source of truth.

## Supported request subset

The first increment covers placement request objects and arrays. It accepts schema
metadata (`$schema`, `$id`, `title`, `description`), local `$defs`/`$ref`, named
objects with explicit `required` and `additionalProperties: false`, bounded arrays,
strings, booleans and bounded integers. Definition names supply generated type
names. References are local and acyclic. Unsupported keywords and inconsistent
constraints are generation errors, including nullable and response variants until
the corresponding generator increment adds tested support.

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

These canonical rules do not claim identical acceptance of Newtonsoft's permissive
parser or its SDK argument binder. Retained parser probes and native acceptance
must identify deliberate grammar corrections before changing the native adapter.
The placement `rotation` remains a string: supported names and ordinary placement
refusals belong to native semantic validation.

## Generation and acceptance

The manifest lists every schema and output explicitly. Generation must reject
invalid schemas and output collisions before publishing files. Check mode compares
all declared outputs against deterministic regeneration without modifying them.
Generated models are boundary values, not validated evidence of pawn work.

G01.02a adds Go, C# and transitional Python generation and cross-language request
checks in sequenced increments. G01.02b–d supply complete replies, real consumer
wiring and isolated SDK acceptance. Production remains on the compatible Python
and native paths until those gates pass. TypeScript generation is added when an
actual dashboard consumer needs a migrated surface.
