# Mechanic-card spike (Gate 0, #3218)

[Documentation](../README.md) · [Thought trigger table](contracts/mood-control.md#thought-trigger-table) · Epic #3217

A spike, kept in the repository whatever its outcome, that asks one question: can the guard conditions and flag
writers behind a memory thought be extracted from the game's own code well enough to build mechanic cards? It is a
dev tool only; `thought_triggers.tsv` and its owner column are untouched, and it adds no persistence, wire format, save
format or action kind. Gate 1 (#3220) ships the cards of the defs with a live consumer in
`brain/src/RimGovernor.Brain.Policy/mechanic_cards.json`, which the same run writes; see
[mechanic cards](contracts/mood-control.md#mechanic-cards). The drafts and coverage table below stay here.

## What it does

`RimGovernor.cmd thoughtaudit -guards` (same prerequisites as `thoughtaudit`: `ilspycmd` and the installed game;
`-decompiled <dir>` reuses an existing decompilation) parses the decompiled `Assembly-CSharp` sources with Roslyn,
syntax only, and for every vanilla ThoughtDef writes a draft card:

- **XML data** from `ThoughtDefs` (stages, `stackLimit`, `durationDays`, `stackedEffectMultiplier`, mood offsets);
  `requiredTraits`, `nullifyingTraits` and `nullifyingHediffs` are lists.
- **Grant sites.** Memory thoughts: every reference to `ThoughtDefOf.<Def>` or `Named("<Def>")` that is not a removal,
  count, read or comparison. Situational thoughts: the active return points of the worker's `CurrentStateInternal`.
- **Guards.** The conditions between the member's entry and the site, as expression trees over resolved symbols
  (enclosing `if`/`else`, ternary, `&&`/`||` operands, early `return`/`continue`/`break`/`throw` guards, `while`/`for`
  headers, `foreach` non-emptiness, and the returned condition of a worker). `&&` chains and negated `||` chains are
  split into one guard each.
- **Count guards (#3281).** A worker local set to 0 whose only write is one increment, reached from a top-level
  `foreach (x in C)` or `for (int i = 0; i < C.Count; i++)` body through `if`/`else` only (no jump before it, no
  `break`/`return` in the loop), is a count. A situational site's guards on it fold into one guard, origin `count`,
  whose condition compares the count symbol (kind `count`) with literals and which carries `over` (C, a local followed
  to its initializer), `where` (the `if` conditions over the element) and `stage` (the site's `ActiveAtStage` index over
  the count, else 0). A virtual call on `this` whose one argument is the element resolves through the def's
  `workerClass` override when that returns one expression (`via`, and its body hash joins the drift hash); otherwise it
  is an unknown `virtual-call`. A local in `where` is an unknown `loop-local`. The pattern is generic: besides
  `DeadMansApparel` it matches `WearingColor_Favorite` and `WearingColor_Ideo` (still `unknown`: external casts and
  loop-locals); no def changed status.
- **Local tracing (#3324).** A declared local reads as its value: the initializer, replaced by a later plain
  assignment and shifted by a later `+=`, `-=`, `++` or `--`, where the write ends in an earlier statement of a block
  enclosing the read and is reached through blocks only (a plain assignment) or blocks and `if`/`else` (a compound
  assignment, which becomes `+ cond(when, delta, 0)`). Syntax only. A local with no initializer (a `foreach` variable,
  a pattern or `out` variable), a write in a loop or lambda the declaration is outside, a `ref`/`out` or tuple write,
  any other write shape, a goto in the member, or a read in a lambda the declaration is outside (the initializer ran
  at capture time) leaves it untraced, and a guard leaf that is an untraced local (kind `local`) is an unknown
  `untraced-local`. A member chain rooted at an untraced local (`toil.actor.Map`) is still a resolved symbol.
  `x.GetStatValue(StatDefOf.S)` with post-processing is one symbol, kind `stat`, decl `StatDefOf.S`.
- **Stage bands (#3324).** A situational site returning `ThoughtState.ActiveAtStage(local)` whose traced value is a
  nested conditional of integer stages over `input < c` comparisons of one input (or the equivalent `>=`, `>`, `<=`
  forms with the literal on the matching side, and their negations), with literal if-guarded modifiers, carries a
  `stage` block: `input`, ascending `thresholds` (`{below, stage}`, adjacent equal stages merged), `else` and
  `modifiers` (`{when, delta}`). Any other shape gets no block, never a guessed one.
- **Writers.** For each guard that ends in a field (or auto-property): every assignment, `++`/`--`, `ref`/`out`
  argument, serialization load and field initializer in the sources, with the written value, the guards on that
  writer's own path, and a `resetWriters` count; the fields a writer copies from are followed one level. A computed
  property is listed as `derived` with its direct inputs.
- **Drift hash.** A SHA-256 prefix of each analyzed member's normalized syntax (the site and every writer), so a game
  update that changes a method is detectable.
- **Unknown, never guessed.** Anything the syntax cannot resolve becomes an `unknown` leaf inside the guard with a
  reason: `external-type` (not declared in `Assembly-CSharp`: BCL, Unity), `unresolved-symbol`, `unresolved-type`,
  `virtual-call` (a `virtual`/`abstract`/`override` member or an interface member, other than a `base.` call),
  `indexer`, `construction`, `lambda`, `switch`, `pattern`, `state-expression`, `untraced-local`, `depth-limit` and
  `unsupported-syntax:<kind>`. A def with no code site is `xml-granted` or `no-code-or-xml-reference`; a site with no
  guard is `conditions-in-callers` (it has callers) or `event-driven` (it has none).

Types are resolved from the declared type text of fields, properties, locals and parameters, through base classes,
extension methods and declared operators. There is no compilation and no references, so a bare name declared twice
under different namespaces is `ambiguous-type`.

### Reading a status

| Status | Meaning |
| --- | --- |
| `resolved` | At least one site, and every guard in every site is fully resolved. |
| `partial` | Some guards resolved and some contain an unknown leaf. |
| `unknown` | No analyzable site, or no resolved guard. |
| `unreachable` | Every site has a guard no writer on its direct caller path can make hold (#3243); primary reason `no-writer-on-caller-path`. |

**Unreachable guards (#3243).** A guard that is a bool field needing `true` is marked `unreachable`, with a reason
naming the writers and the callers checked, when the field has no initializer, no serialization writer and at least
one writer (a field no code writes is set by the XML loader), every writer is listed, in a method, with a resolved
receiver, and none of them is on the site's direct caller path. That path is the site's own member, the members
holding its direct call sites (a call in a lambda counts as its enclosing member) and the methods those members call
directly, all matched by simple name. The site must be a method with at least one call site and no delegate, `?.` or
generic reference to its name. Writes in earlier, separate calls on the same instance are not modelled, so the rule is
shallow by design: a transitive caller walk explodes through `JobDriver.SetupToils` and `Pawn_JobTracker.StartJob`
(5,804 members at depth 8). Probes remain the final word.

`resolved` is a claim about the granting member only. It does not cover conditions in callers (each site carries its
caller count), the bodies of resolved calls and properties (a call is a symbol, not an inlined condition), a def
field's XML initialization (writers of def fields such as `IngestibleProperties.tableDesired` are the XML loader), or
the lambda that defers a grant (recorded as a resolved `lambda` guard saying so). A card is a draft until a probe
verifies it (`DeadMansApparel`, #3283).

## Outputs

| File | Content |
| --- | --- |
| [`mechanic-card-drafts.json`](mechanic-card-drafts.json) | Coverage summary plus the draft cards for `KnowBuriedInSarcophagus`, `AteWithoutTable`, `EnvironmentDark`, `DeadMansApparel`, `EnvironmentCold` and `EnvironmentHot` (`-only` picks others). |
| [`mechanic-card-coverage.tsv`](mechanic-card-coverage.tsv) | One line per ThoughtDef: kind, owner (read from `thought_triggers.tsv`), status, counts and primary unknown reason. |
| [`mechanic_cards.json`](../../brain/src/RimGovernor.Brain.Policy/mechanic_cards.json) | The shipped cards (#3220): live-consumer defs only, typed XML numbers, status `extracted` or `verified` (`-shipped` overrides the path). |

All are generated; two runs over the same decompiled tree are byte-identical (checked by comparing `sha256` of the
two runs). Regenerate with `RimGovernor.cmd thoughtaudit -guards`.

## Results (game 1.7.4980 rev60 XML, 923 ThoughtDefs, 9217 decompiled sources)

| | Defs | Share |
| --- | ---: | ---: |
| resolved | 113 | 12.2% |
| partial | 304 | 32.9% |
| unknown | 505 | 54.7% |
| unreachable | 1 | 0.1% |

By kind: memory 77 resolved / 51 partial / 450 unknown / 1 unreachable (579 defs); situational 36 / 253 / 55 / 0 (344
defs). The unreachable check (#3243) moved one def, `KnowBuriedInSarcophagus`, from `resolved`; no other def and no
other owned lever changed. Only one other guard met the check's field conditions apart from its writers:
`BuildingProperties.bed_crib` (`ThoughtWorker_CribQuality`), which no code writes (the Biotech XML sets it), so it is
excluded. Over the
749 sites found, 1552 of 2408 guards (64.5%) resolved.

Local tracing (#3324) moved 27 defs, all down: 22 from `resolved` (135 to 113) and 5 from `partial` to `unknown`.
Before it a bare local was a resolved leaf whatever it held. Now 13 such defs hold an untraced local (for example
`SleptInCold`, whose comfort bound is summed over genes in a loop), and 14 a local whose traced initializer is itself
unknown (`indexer`, `virtual-call`, `construction`, `?.`; for example the `Trial*` defs). `EnvironmentCold` and
`EnvironmentHot` stay `resolved`, now through their traced input, and the 16 owned defs keep their statuses.

Dominant reasons a def is not `resolved` (the most frequent unknown reason of the def):

| Reason | Defs |
| --- | ---: |
| `no-code-or-xml-reference` (memory def never named in the code or in other def XML) | 266 |
| `external-type` (BCL or Unity type: `HashSet` 142 leaves, `List` 53) | 175 |
| `xml-granted` (named only in other def XML) | 156 |
| `unsupported-syntax` (`?.` 69 leaves, `switch` expressions 12, `out var` 10) | 60 |
| `state-expression` (worker returns a value, not a condition) | 56 |
| `conditions-in-callers` | 24 |
| `untraced-local` | 20 |
| `indexer` | 18 |
| `virtual-call` | 11 |
| `pattern` 6, `construction` 4, `switch` 4, `event-driven` 3, `lambda` 3, `unresolved-symbol` 3 | 23 |

The two biggest blocks are not a parser limit: 422 memory defs have no grant in the code at all (156 appear in other
def XML, 266 nowhere; the 266 were not investigated). The 856 unresolved guards hold 913 unknown leaves: `external-type` 226,
`state-expression` 200, `virtual-call` 160, `untraced-local` 98, `unsupported-syntax` 92, `indexer` 79 (the remaining 58 are smaller). `?.` and `switch` expressions are
cheap to model; `external-type` and `virtual-call` are not and stay unknown by design.

### Benchmarks

`KnowBuriedInSarcophagus` (memory, `unreachable` since #3243, `resolved` before) matches the code facts of #2209, but
not its conclusion: the `thisIsFirstBodyEver` guard is `unreachable` because its only writer, `TryAcceptThing`, is not
on the path from `Notify_HauledTo`'s caller `Toils_Haul.DepositHauledThingInContainer`, which inserts through
`ThingOwner.TryTransferToContainer`. A native probe confirmed that hauling never grants the memory (#3242). This is the
Gate 1 negative benchmark.

- Site `Building_Sarcophagus.Notify_HauledTo` (`public override`, 3 callers); XML `stackLimit` 4, `durationDays` 8,
  one stage, `baseMoodEffect` 4.
- Guards, all `early-exit` and resolved: `thisIsFirstBodyEver` (field), `hauler.IsColonist` (`Pawn.IsColonist`),
  `base.Corpse.InnerPawn.def.race.Humanlike` (`RaceProperties.Humanlike`), `!base.Corpse.everBuriedInSarcophagus`
  (field), then the grant loop's `foreach` over `base.Map.mapPawns.FreeColonists` and `freeColonist.needs.mood != null`.
- Writers: `thisIsFirstBodyEver` is written once, `TryAcceptThing` sets it to `!everNonEmpty` under
  `base.TryAcceptThing(...)`, with no reset; `everNonEmpty` is written by `TryAcceptThing` (`true`) and loaded by
  `ExposeData`, no reset; `everBuriedInSarcophagus` is set to `true` in `Notify_HauledTo` under the same four guards
  and loaded by `Corpse.ExposeData`, no reset. `Pawn.IsColonist` and `RaceProperties.Humanlike` are computed
  properties, listed as `derived` (`Pawn.IsColonist` has a multi-statement getter and is not expanded).

Two further owned-lever thoughts from the `thought_triggers.tsv` owner column resolve their guards:

- `AteWithoutTable` (owner rooms/comfort; `resolved`): `Toils_Ingest.FinalizeIngest`, eight guards including
  `toil.actor.jobs.curJob.GetTarget(ingestibleInd).Thing.def.ingestible.chairSearchRadius > 10f` (the delegate's locals
  traced; the captured `toil` is not), `!(ingester.Position + ingester.Rotation.FacingCell).HasEatSurface(toil.actor.Map)`,
  `ingester.GetPosture() == PawnPosture.Standing`, `!ingester.IsWildMan()` and the `tableDesired` flag, plus
  the deferred-delegate note.
- `EnvironmentDark` (owner rooms/lighting; `resolved`): `ThoughtWorker_Dark.CurrentStateInternal` returns
  `p.Awake() && p.needs.mood.recentMemory.TicksSinceLastLight > 240 && !PawnUtility.IsBiologicallyOrArtificiallyBlind(p) && (p.Ideo == null || !p.Ideo.IdeoPrefersDarkness())`.

The Gate 1 lever (#3217 re-grooming), `DeadMansApparel` (owner MaintainEquipment; `resolved`, shipped since #3281):
`ThoughtWorker_ApparelThought.CurrentStateInternal` gives two sites, each one count guard over
`p.apparel.WornApparel` (`Pawn_ApparelTracker.WornApparel`) where `apparel.WornByCorpse`, resolved through
`ThoughtWorker_DeadMansApparel.ApparelCounts`: count != 0 and >= 5 at stage 4, count != 0 and < 5 at stage
`count - 1`. Stages -5/-8/-11/-14; nullifiers `Bloodlust` and `Inhumanized` (Anomaly). Native clamps stage 4 to the last
stage (`ThoughtState.StageIndexFor`). The native probe `apparel/dead_mans` (#3283) observed -5/-8/-11/-14 for one to four tainted
items, -14 (stage 3, the clamp) for five, and no offset with Bloodlust, so the card ships `verified`.

The Gate 2 temperature cards (#3324), `EnvironmentCold` and `EnvironmentHot` (owner shelter/temperature; `resolved`,
shipped `extracted`): `ThoughtWorker_Cold.CurrentStateInternal` has one site whose stage band has input
`p.GetStatValue(StatDefOf.ComfyTemperatureMin) - p.AmbientTemperature` (Hot: `p.AmbientTemperature` minus
`ComfyTemperatureMax`), thresholds below 10 stage 0, below 20 stage 1, below 30 stage 2, else 3, and a -2 modifier when
`ModsConfig.IdeologyActive`, `p.Ideo != null` and `p.Ideo.HasPrecept(PreceptDefOf.Temperature_Tough)`. Its guards are the
input above 0 and the shifted stage at least 0. Stages -4/-8/-12/-16. Before #3324 both were `resolved` over the bare
locals `num` and `num2`.

Across the 16 owned defs, 9 are `resolved` (also `SleptInBarracks`, `SleptOutside`, `EnvironmentCold`,
`EnvironmentHot`, `DeadMansApparel`, `BabySick`, `Sick`), 6 `partial` and 1 `unknown`. All three benchmark cards were
checked against the decompiled source by hand.

## Verdict against the Gate 0 criteria

**GO**, on the epic's own terms (an exact sarcophagus card plus two further owned-lever thoughts, no coverage
threshold):

1. Sarcophagus card: met. Site, four guards, writer chain and XML fields match #2209, with no hand edits.
2. Two further owned thoughts: met (`AteWithoutTable`, `EnvironmentDark`; 9 of 16 owned defs resolve).
3. Coverage and reasons: 136 resolved, 293 partial, 494 unknown of 923 at Gate 0 (one resolved def is now
   `unreachable`, see above); dominant unknown reasons listed.

What the spike does not show, for Gate 1 grooming: `resolved` stops at the granting member, so caller-side
conditions (for example who may haul to a sarcophagus) are still unknown and need either caller analysis or probes;
resolved properties and calls are symbols, not expansions; about half the memory defs have no code grant to analyze;
and an extracted card is still unverified.

## Removing it

The spike is self-contained: delete `tools/rg/Commands/ThoughtAudit/Guard*.cs`, the `-guards` branch of
`ThoughtAuditCommand`, the `Fields` member of `ThoughtDef`, the `Microsoft.CodeAnalysis.CSharp` reference in
`tools/rg/rg.csproj` (its version repeats the central pin in `brain/Directory.Packages.props`, because `tools/` sits
outside that file's scope), `GuardAnalysisTests.cs` with the `ThoughtAudit` `Compile Include` lines in the domain test project,
this page and the two generated files. Since Gate 1 the extraction also feeds the shipped `mechanic_cards.json` and its
`MechanicCards` loader in Brain.Policy, which would have to go with it, as would `MechanicCardGuards` (#3223) and its
tests.
