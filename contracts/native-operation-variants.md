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
not a claim about SDK omitted/null conversion. N01.04 owns rows in this table
except the observation rows (N01.03) and clock/presentation rows (N01.05).

| Tool | Implemented selector and owning method | Conditional behavior and uncovered acceptance |
| --- | --- | --- |
| `home/population` | [PopulationTool.cs](../integrations/rimgovernor-native/src/Bridge/PopulationTool.cs): `Population`. interaction null reads; supplied text resolves exact native PrisonerInteractionModeDef, then restricts to AttemptRecruit and MaintainOnly objects. | Advertisements say Recruit/MaintainOnly, but accepted strings are the installed objects' defName values; no literal Recruit alias is implemented. Discover `interactions[].name`. Empty interaction is a supplied invalid request. dryRun defaults true. Verify returned definition names, native recruitment eligibility, prior-setting mismatch and actual recruitment separately. |
| `home/runtime_health` | [RuntimeHealthTool.cs](../integrations/rimgovernor-native/src/Bridge/RuntimeHealthTool.cs): `Read`. No parameters. | Read-only: every required authority hook by name with installed/resolved/error, whether the typed clock hooks are installed (lazily, by the first clock epoch), the clock event journal (`journal.initialized`, `newestCursor`), and the current authority snapshot. Never installs or repairs; `NativeAuthorityHooks.Install` retries missing hooks from the next admission or update poll, and an unresolved target is restart-required. Accepted by the `lifecycle/runtime-fault` acceptance case (`RuntimeFaultFixture`: `test/runtime_fault_unpatch`). |
| `home/list_buildings` | [ListBuildingsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListBuildingsTool.cs): `Build`. Trim/lower status all/built/blueprint/frame/pending; null defaults all. Category artificial/all; null defaults artificial. Blank and unknown refuse. | Observation options aggregate/inspect/billIngredients change detail; `byPlayerOnly` overrides playerOnly when supplied, except playerOnly=true/byPlayerOnly=false explicitly refuses. N01.03: category/status combinations, alias precedence and unavailable bill ingredients without mutating jobs. |
| `home/list_things` | [ListThingsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListThingsTool.cs): `Build`. Trim/lower category haulable/food/weapons/all/buildings, null defaults haulable; blank/unknown refuses. ownership is different: only trimmed case-insensitive all disables oursOnly; every other value, including null/blank/unknown, selects ours. | includeHeld and corpses add independent observation branches. N01.03: preserve/document ownership fallback before changing it, category/held parity and unavailable corpse facts. |

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

## Single-operation and observation coverage

These rows explicitly account for the remaining exports; absence of an op string
does not make boolean branches or runtime object variants interchangeable.

| Tool | Owner, implementation and variants | Concrete migration acceptance still uncovered |
| --- | --- | --- |
| `home/confirm_colony_names` | N01.04; [ColonyNamingTool.cs](../integrations/rimgovernor-native/src/Bridge/ColonyNamingTool.cs), Confirm. Exact known bootstrap naming window and suggestions; dryRun=true default. | Stale/other windows, invalid names, unchanged preview and native named objects/dialog closure. |
| `home/colony_identity` | N01.03/06; [ColonyIdentity.cs](../integrations/rimgovernor-native/src/Bridge/ColonyIdentity.cs), Identity. No selector; read can attach missing identity component/create colony ID. | No-game/map failure; old-save component attachment exactly once, stable colony ID and rotated load token. |
| `home/list_pawns` | N01.03; [ListPawnsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListPawnsTool.cs), ListPawns and PawnFilters. Boolean scope/detail selectors and nameFilter compose; installed pawn/hediff/need/work/animal definitions are dynamic. | Incompatible/empty filter combinations, absent needs/dead/held categories and native read-only invariance. |
| `home/wall_upgrade_sites` | N01.03; [WallUpgradeTool.cs](../integrations/rimgovernor-native/src/Bridge/WallUpgradeTool.cs), Sites. Exact target; native straight/corner geometry produces candidates. | Unsupported geometry/material/access and no designations on reads. |

## Compatibility decisions still requiring evidence

The selector lists above were read from source, not inferred solely from SDK
descriptions. Several advertised summaries are narrower than implemented input:

- Bill string booleans accept yes/no/true/false; pawn/building booleans also accept
  1/0. Numeric rotation strings are accepted by place_building. These aliases need
  deliberate schema treatment and fixtures before strict validation replaces them.
- Native enum TryParse in both clock tools accepts numeric text without IsDefined.
  The other clock enum members and numeric overflow need actual runtime probes;
  do not describe advertised names as the complete accepted language.
- list_things ownership unknown values fall back to ours. A future strict refusal
  is a behavioral change, even though it is safer than silent fallback.
- Population's advertised Recruit text must be reconciled with the discovered
  AttemptRecruit object's defName; the source does not implement that alias.
- Field defaults depend on presence: place_building requires explicit dryRun;
  order defaults to execution; trade has no dryRun; most other writes default to
  preview. Shared argument plumbing must preserve these distinctions until a
  coordinated schema change. Unknown argument metadata is not rejection by itself.
- Preview/read labels do not mean no runtime bookkeeping: identity can initialize
  save metadata; render status can initialize its driver. Separate permitted
  bookkeeping from gameplay effects in the acceptance contract.

N01.02/03/04/05 must capture actual discovery and representative replies for
every branch family, including omitted versus null versus blank, case/alias
behavior, unknown keys, refused and unavailable results. Dynamic native names
require installed-definition fixtures, not copied hardcoded lists. Method-source
verification does not establish serialization, binding, pawn completion, stale
queued dispatch, lost-reply recovery or save/load behavior. Those remain open in
[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22); the linked existing
native scenarios are candidates, not evidence produced by this audit.
