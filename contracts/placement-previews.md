# Placement preview request contract

This is the proposed canonical request boundary for `home/placement_previews`,
owned by G01.02. The [schema](schemas/placement-previews-request.v1.schema.json)
and [boundary cases](fixtures/placement-request-cases.json) define generated
validation. Existing Python and native consumers have not yet migrated to it.
Reply DTOs, native integration and game-level acceptance remain separate G01.02
steps in the [backlog](../docs/BACKLOG.md#g01--go-controller-rewrite).

The existing implementation is
[PlacementPreviewsTool.cs](../integrations/colony-bridge/src/PlacementPreviewsTool.cs).
[placement_previews.py](../controller/rimgovernor/placement_previews.py) constructs
the nested JSON string and preserves candidate order. No new endpoint, game order
or change to placement rules is introduced by this request contract.

## Wire representation and validation

The native argument object contains one required, non-null `placements` string:

```json
{"placements":"[{\"defName\":\"Wall\",\"x\":1,\"z\":2,\"rotation\":\"north\",\"stuff\":\"WoodLog\"}]"}
```

Validate the outer object, then parse and validate the contents of its string.
An outer-only pass does not establish that a complete request is valid. Do not
replace the string with an array-valued native argument or reserialize existing
recordings before checking their bounds.

| Boundary | Canonical requirement |
| --- | --- |
| Outer argument object | Exactly `placements`; case-sensitive, required and non-null string. |
| Outer string | At most 32,768 UTF-16 code units after decoding the outer JSON string, before parsing its contents. |
| Inner document | One array containing 1–16 candidate objects. Order and duplicates of whole candidates are preserved. |
| Candidate object | Exactly `defName`, `x`, `z`, `rotation`, `stuff`; every field required and non-null. |
| `defName`, `rotation`, `stuff` | Strings of at most 200 UTF-16 code units; no conversion from numbers or booleans. |
| `defName`, `rotation` | At least one character that .NET `String.IsNullOrWhiteSpace` does not treat as whitespace. |
| `stuff` | Empty or whitespace-only strings are structurally allowed. Empty means native default material preview. |
| `x`, `z` | JSON integer tokens within −2,147,483,648 through 2,147,483,647. Fractions, exponent spellings, strings and booleans are rejected. `-0` is allowed. |
| JSON grammar | Strict JSON, one complete value, no duplicate decoded object keys. Comments, single quotes, unquoted names and trailing commas are rejected. |
| Unicode | Valid scalar strings, allowing paired surrogate escapes but rejecting unpaired surrogate escapes. No Unicode normalization. |

UTF-16 bounds count a supplementary scalar twice. JSON Schema's ordinary
`maxLength` counts code points instead, so generated validators need the schema's
explicit UTF-16 rule. Likewise a mathematically integral `1.0` or `1e0` is not a
native integer token. Validate token kinds before DTO conversion.

The whitespace set is U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680,
U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000. Python's broader
`str.isspace()` is not a substitute: U+001C is not whitespace for this contract.
U+200B and U+FEFF are also non-whitespace. Such characters can pass structural
validation while the native definition or rotation lookup subsequently refuses.

## Structural errors and native refusals

The current native tool uses a string parameter defaulting to null. It parses
with `JArray.Parse` and duplicate-property rejection, checks five exact fields,
UTF-16 lengths, whitespace and integer tokens, then schedules a single native
main-thread turn. Structurally invalid candidates fail the entire batch before
any candidate preview is evaluated.

Structural validation does not restrict rotation to an enum, require nonnegative
coordinates, resolve definitions or materials, or establish `canPlace`.
`PlaceBuildingTool.cs` resolves native semantics after the structural check.
For example, `rotation:"not-a-native-rotation"` and an unknown definition are
structurally valid and receive candidate refusals. `rotation:" ALL "` is passed
unchanged to the native trim/case-folded rotation parser. Negative int32
coordinates are structurally valid but may be outside the map.

Candidate refusal, `canPlace:false`, SDK operation failure and malformed-request
failure are distinct. Successful preview evaluation does not reserve resources
or cells, create construction, advance time or move the camera.

## Deliberate grammar and argument corrections

The canonical strict JSON and scalar-string policies must not be described as
already observed legacy parser behavior. Newtonsoft defaults can accept input
spellings beyond strict JSON or replace invalid Unicode. The independent
`placement-parser-baseline.json` probe records actual behavior separately;
`legacyObserved.status:"pending-probe"` in the canonical cases is explicitly
not an observation. Any differences require native adapter agreement and
cross-language acceptance before the generated consumer lands.

The native early malformed-request return contains `success:false` and `error`
without unknown-argument decoration. Main-thread replies pass through
`BridgeCommon.WithUnknownArguments`, which reports unknown outer arguments and
may attach an inspection-unavailable warning. The proposed closed outer schema
rejects unknown arguments instead of relying on that native diagnostic. This is
an explicit adapter-boundary correction, not unchanged direct-native behavior.
Existing Python schema validation already rejects unknown arguments where its
gameplay gateway has discovered the tool schema.

## Reproducible fixture inputs

Every case names either `outer_arguments` or `placements`, its raw JSON document,
canonical structural acceptance and rationale. Invalid JSON remains inside a
JSON string so the fixture file itself is valid and no parser repairs the input.

`input.kind:"raw"` uses `input.json` exactly. For `kind:"template"`, process
`replacements` in array order: replace the single literal `token` occurrence
with `text` repeated `count` times. Do not trim, escape, reserialize, translate
newlines or normalize the expanded text. Tokens are unique and cannot occur in
replacement text. This keeps the 200/32,768-unit boundary cases reviewable
without checking in giant strings. Validation operates on the complete expanded
document, not on a synthetic length supplied by the fixture.

Cases cover native int32 limits and token spelling, required/null/unknown fields,
escaped duplicate keys, array bounds, supplementary characters, whitespace,
surrogates and nonstandard JSON. They assert structural acceptance only; generated
Python/Go/C# consumers and native parity must run them before integration is
accepted. The retained native successful ButcherSpot reply is separate evidence
in [native-replies-baseline.json](fixtures/native-replies-baseline.json), not
coverage for these invalid-request cases.
