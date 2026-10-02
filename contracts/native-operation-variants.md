# Implemented native operation variants

Source audit of the production native tools under
`integrations/rimgovernor-native/src/Bridge`. This page records implementation
branches, selectors, defaults and aliases; it does not establish actual SDK
binder behavior.

All rows are **source-audited, migration acceptance pending**. G01.02 owns the
shared schemas. N01.02 owns the first typed boundary, N01.03 observation migration,
N01.04 gameplay operations, and N01.05 clock/presentation operations, as specified
below; these phase owners identify the subsequent implementation and acceptance work.

Source links refer to the compiled implementation.
Closed selectors below are tool vocabulary. Native definition names, IDs, labels,
UI fields and generated schedule keys remain discovered values, not closed enums.
This is not a field-by-field result schema or a complete numeric-limit audit.

## Operation selectors

Unless a row says otherwise, an unsupported selector returns a refusal/failure;
that does not prescribe one uniform JSON failure shape. `Trim/lower` means
whitespace is trimmed and invariant lower case is used; `exact` means ordinary
case-sensitive equality with no normalization. Defaults are C# entry defaults,
not a claim about SDK omitted/null conversion.

`PrivatePlayerInput.Key` accepts KeyA–KeyZ, Digit followed by one char.IsDigit character, parsed F1–F12,
and the exact keys Escape, Enter, Space, Backspace, Delete, Tab, ArrowLeft,
ArrowRight, ArrowUp, ArrowDown, ShiftLeft, ShiftRight, ControlLeft, ControlRight,
AltLeft, AltRight, Comma, Period, Minus, Equal, Home, End, PageUp, PageDown,
BracketLeft, BracketRight, Slash, Backslash, Semicolon, Quote and Backquote.
F suffix parsing uses int.TryParse rather than an exact canonical F-name list.
The display keymap must resolve the result; these strings do not guarantee that
an installed display supports the key. Button range and wheel behavior remain
input contract fields, not extra action selectors.

## Field-driven operations

N01.04 owns these operation migrations except placement previews (first vertical
slice N01.02), research's read projection (N01.03), and world presentation (N01.05).

| Tool | Implemented branch and owning method | Unknown/unavailable behavior and uncovered acceptance |
| --- | --- | --- |
| `home/placement_previews` | [PlacementPreviewsTool.cs](../integrations/rimgovernor-native/src/Bridge/PlacementPreviewsTool.cs): `Preview` delegates to HomePlaceBuildingTools.Preview. JSON string contains 1..64 rows, exactly defName/x/z/rotation/stuff; rotation shares the parser above, but input string cannot be blank. | Duplicate/unknown/missing keys, wrong scalar types, overflow or blank required strings refuse. No dryRun/write/godMode option; always preview. N01.02: cross-language fixtures, actual SDK adapter invocation, invalid input and unchanged native refusal/no-effect outcomes. |

## Compatibility decisions still requiring evidence

The selector lists above were read from source, not inferred solely from SDK
descriptions. Several advertised summaries are narrower than implemented input:

- Bill string booleans accept yes/no/true/false; pawn/building booleans also accept
  1/0. Numeric rotation strings are accepted by place_building. These aliases need
  deliberate schema treatment and fixtures before strict validation replaces them.
- Native enum TryParse in both clock tools accepts numeric text without IsDefined.
  The other clock enum members and numeric overflow need actual runtime probes;
  do not describe advertised names as the complete accepted language.
- Field defaults depend on presence: place_building requires explicit dryRun;
  order defaults to execution; trade has no dryRun; most other writes default to
  preview. Shared argument plumbing must preserve these distinctions until a
  coordinated schema change. Unknown argument metadata is not rejection by itself.
- Preview/read labels do not mean no runtime bookkeeping: render status can
  initialize its driver. Separate permitted
  bookkeeping from gameplay effects in the acceptance contract.

N01.02/03/04/05 must capture actual discovery and representative replies for
every branch family, including omitted versus null versus blank, case/alias
behavior, unknown keys, refused and unavailable results. Dynamic native names
require installed-definition fixtures, not copied hardcoded lists. Method-source
verification does not establish serialization, binding, pawn completion, stale
queued dispatch, lost-reply recovery or save/load behavior. Those remain open in
[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22); the linked existing
native scenarios are candidates, not evidence produced by this audit.
