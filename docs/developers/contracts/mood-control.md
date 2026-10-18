# Mood relief contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Normal production uses the [shared portfolio comparison](../architecture/portfolio-selection.md)
for supported need services and conditional thought consequences. Native thought
windows, actual mood response and physiological sleep activation remain separate
facts; a selected bed service does not certify sleep. These forecasts do not
replace the per-pawn mood Needs and independent relief behavior described below.

Each tracked pawn's mood is the `people/mood/<pawn>` Need of the people family (#3304;
the `EnsureMood` Response and its mood relief planner are deleted), a Wellbeing Need
whose predicate is the review's mood state; an unread review is one Unknown
`people/mood` Need. It is unmet within 0.15 above the pawn's observed
minor-break threshold, or when the native current thought target is below that
threshold and falling relative to current mood, and met above it: one bar with no
separate entry and recovery margins (#3265, decision 26). Missing pawns, thresholds
or need reads never certify recovery. Native thresholds incorporate individual traits and
ideology; current thought pressure predicts neither a break probability nor its
timing. The [pawn profile](work-assignment.md#pawn-profile) exports the trait flags
(Pyromaniac, chemical interest, NightOwl) for relief and containment ordering.

Typed nullable storage fields preserve unknown, zero and false. Manual clears current
method proposals and suspends shared work while retaining mood history; world
replacement and tick rewind reset that history. Departed pawns with unresolved risk
remain unknown; departed recovered occurrences close.

The brain ranks measured food, rest and recreation deficits by distance from a
0.5 recovery target. A need below 0.3 is a cause; an unread level keeps its earlier cause. Each correction uses one pawn and existing
native resources; future mood benefit and labor duration remain unknown. Food,
shelter and temperature provisioning use the shared colony concerns.

**Psychic drone.** A pawn whose observed thought rows carry the drone's own
`PsychicDrone` thought (kept only while it pulls the mood down; the same def's
soothe stage is dropped) widens its causes: every need short of the 0.5 target is a
cause while the thought lasts (its margin, the drone's offset as a mood fraction, is
capped at the ordinary 0.15). Pawns without the thought keep the ordinary cause bar,
whatever the condition census says.

A mood Need commits nothing. Its step names what it waits on: the owner Need of a
provisioned pawn (below) while that Need is at work, the unsupported preventative cast
when no measured need relief remains, else the shared portfolio's need relief.

## Facility provisioning

The routine pawn read carries each colonist's grouped thought rows (memories and the
situational cache, the `social` block). The census carries every thought row (positive ones too, e.g.
`KnowBuriedInSarcophagus`); the provisioning review reads only the rows that pull mood down
and maps removable environment thoughts to the Need whose facility removes
them (a thought names every Need providing it):

| Thought | Need |
| --- | --- |
| `AteWithoutTable`, `NeedJoy` | `rooms/comfort` |
| `SleptOutside`, `SleptOnGround`, `SleptInBarracks`, `NeedRoomSize` | `shelter/sleeping` |
| `EnvironmentDark` | `rooms/lighting` |
| `EnvironmentCold`, `EnvironmentHot` | `shelter/temperature` |

When those thoughts carry at least half of the pawn's negative thought offset, the
pawn's mood state records the owners (most negative first) and the method proposal is
`facility_provision` naming the first owner instead of a relief job. The pawn's mood
Need waits on that owner Need (`WaitingOnNeed`) only while some owner's latest progress
row is unmet with an open Method (#3231 decision 18; the row is the previous review's,
so the defer lags the owner by one review); otherwise the proposal falls back to the
measured need method. Mood pressure does not raise an owner Need's deficit; the owner's
own census decides whether it is unmet and what it builds. An
unreadable social block keeps the previous provisioning; a readable one with no such
pressure clears it.

`SleptInBarracks` is answered by the existing bedroom builders (`NextBedroomStep`):
barracks sleepers are unhoused, so the planned private bedrooms are built within the
housing budget and research gates; the thought only names `shelter/sleeping` as the
owner a mood waits on, and adds no new builder.

The owner of a thought is the audit table's `owner` column (see below); there is no
second table in code. Every negative thought with an empty owner, or no table row at
all, is **unowned**: when owned provisioning does not apply, the pawn's mood state
records its unowned thoughts (`Unowned`, most negative first) and, once no measured
need method remains, the proposal is the explicit `unowned_thought_pressure` blocker
naming the largest instead of `no_measured_correctable_need`; measured relief runs
first. Social memories are unowned in this sense but stay native relief and recovery
evidence. The apparel thoughts `ApparelDamaged` (ratty and tattered), `DeadMansApparel`
(tainted) and `WrongApparelGender` are owned by the `military/equipment` Need: its levers already
exist (the outfit excludes tainted gear unless the comparator chose otherwise, see
[mechanic cards: now live](#mechanic-cards-now-live), floors hit points at `apparelMinHP`, offers only
gender-correct definitions, and `ClothingRunway` replaces garments before they tatter),
so dominant apparel pressure names that Need and adds no new action.
Cosmetic preferences and Ideology apparel precepts stay unowned. The pain and
sickness thoughts `Pain`, `Sick` and `BabySick` are owned by the `medical/care` Need:
tending, disease rest, the hospital bed and the medicine stock it holds already treat
their causes (injuries, infections, infant illness), so dominant pain or sickness
pressure names that Need and adds no new action. The medicine runway still
follows realized tending, not mood; no painkiller policy exists (combat-only drugs are
untouched). `MasochistPain`, `Pain_Idealized` and permanent-condition hediff thoughts
stay unowned. Schedules are never written. Social recreation, tolerated
recreation kinds and environmental eligibility remain native job-giver choices.
Thoughts without a measured eligible corrective method produce an explicit blocker,
including relationship and ideology choices requiring player direction. A facility
placement is never a mood or need postcondition.

### Thought trigger table

`brain/src/RimGovernor.Brain.Policy/thought_triggers.tsv` lists every vanilla ThoughtDef (all installed
packs) with how the game grants it and the game state it reads. `RimGovernor.cmd thoughtaudit`
(`tools/rg/Commands/ThoughtAudit`) regenerates it from the installed game: it decompiles `Assembly-CSharp.dll`
with the global `ilspycmd` tool (`dotnet tool install -g ilspycmd`; not pinned in the repo)
and reads the defs under `Data/*/Defs`. Columns:

- `def`.
- `kind`: `situational` when the def has a `workerClass`, else `memory`.
- `grant`: the worker class, or the memory trigger categories (`ingest`, `sleep`, `social`,
  `ritual`, `other`) with the code and XML sites that grant it.
- `dependency`: sorted `;` tokens (`room_stat:<Stat>`, `room`, `room_role:<Role>`,
  `need:<need>`, `apparel`, `temperature`, `hediff`, `light`, `other`) read from the worker,
  or from the granting method body, so a method granting several thoughts lists the state
  of all of them.
- `owner`: the Need that can remove the thought (a NeedId; a test keeps it so). Hand-kept: regeneration preserves it
  and leaves new defs unowned.

`policy.ThoughtAuditRow` embeds the table (it stays beside the policy package that owns the
owner column); the mood ledger reads a def's owners from it, and a def with no table row
(modded, or new since the last regeneration) has none. The bridge's
`DefinitionCatalog.ThoughtFacts` projects a catalog `ThoughtDef` row into
`policy.ThoughtFacts`: `WorstOffset` (lowest stage `BaseMoodEffect`; null stages skipped),
`NullifyingTraits`, `NullifyingPrecepts`, `RequiredTraits` and `MinExpectation`. Policy stays
catalog-free.

There is no check mode; a stale row shows as unowned. Modded thoughts are out of scope.
`HighExpectations`, `SkyHighExpectations` and `AteAwfulMeal` are not ThoughtDefs in the
installed game, so the table has no rows for them and no owner entry exists; high
expectations reach the review only through the pawn's `HighExpectations` flag.

#### Mechanic cards

`brain/src/RimGovernor.Brain.Policy/mechanic_cards.json` holds a mechanic card for each ThoughtDef with a live
consumer (an allowlist in `ThoughtAuditCommand`; today `KnowBuriedInSarcophagus`, the Gate 1 lever `DeadMansApparel`
and the Gate 2 temperature cards `EnvironmentCold` and `EnvironmentHot`, epic #3217). `RimGovernor.cmd
thoughtaudit -guards` regenerates it with the [spike's](../mechanic-card-spike.md) syntax-only extraction; the output is
byte-identical for one decompiled tree. The shipped card carries the def, its status, the XML fields it reads (the
`nullifyingTraits` and `nullifyingHediffs` lists, stages with `baseMoodEffect`), and each granting site with its guard
expression trees and the writers of the fields those guards read (guards and followed fields). The draft cards file
(`-cards`) also carries what only the tool reads: kind, access, caller count, `bodyHash` drift hashes, guard origins and
text, unreachable reasons, write kinds and values, and writer totals.
A count guard (#3281) also carries `count`: `over` (the collection a worker loop iterates), `where`
(the per-element predicate, resolved through the def's `workerClass` override) and `stage` (the stage
index the site returns, over the count); its expression compares the count symbol with literals. Unresolved leaves
stay `unknown` with their reason; a local the extraction could not trace to its value is `untraced-local`. Modded
defs get no cards.

A site that returns `ThoughtState.ActiveAtStage(local)` over a banded local (schema 2, #3324) also carries `stage`:
`input` (the traced expression, for example the stat symbol `p.GetStatValue(StatDefOf.ComfyTemperatureMin)`, kind
`stat`, minus `Thing.AmbientTemperature`), ascending `thresholds` of `{below, stage}`, the `else` stage and
`modifiers` of `{when, delta}` (an if-guarded `+=`/`-=` on the local). The stage is the first threshold the input is
under, else `else`, plus each delta whose `when` holds; the site's guards still decide whether the thought is active
(for the temperature cards, input above 0 and final stage at least 0). `EnvironmentCold` and `EnvironmentHot` carry
thresholds 10/20/30, else 3, and -2 when Ideology is active and the pawn's ideo has `Temperature_Tough`; both ship
`verified` since #3328.

`MechanicCards.ByDef` embeds the file and parses it on first use into `MechanicCard` records. A malformed file, an
unknown or missing field, an unknown status or a schema other than `MechanicCards.Schema` throws; it never yields an
empty catalog. A card's `status` is `extracted` until a probe verifies it; such a card loads, but `Live` is false
and consumers ignore it for live play. `DeadMansApparel` ships `verified` (`Live`) since #3283 and drives the tainted-apparel choice since #3285;
`EnvironmentCold` and `EnvironmentHot` ship `verified` since #3328 (no consumer yet; the evaluator is #3327).

A card is `unreachable` (#3243) when every site has a guard that no writer on the site's direct caller path can make
hold; the guard carries an `unreachable` reason naming the writers and the callers checked. The check is shallow: the
path is the site's own member, the members holding its direct call sites (a call inside a lambda counts as its
enclosing member) and the methods those members call directly, matched by simple name. It applies only to a bool
field guard needing `true` whose field has no initializer, no serialization writer and at least one listed writer, all
in methods with a resolved receiver, at a method site with a call site and no delegate, `?.` or generic reference.
Writes in earlier, separate calls on the same instance are not modelled, so probes stay the final word. An
`unreachable` card ships so the brain knows the lever is dead: `Live` is false, the verified table rejects a row for
it, and the effect-source search reports `effect_source_card_unreachable:<def>` instead of a candidate.
`KnowBuriedInSarcophagus` is `unreachable`: `thisIsFirstBodyEver` is written only by
`Building_Sarcophagus.TryAcceptThing`, which neither `Notify_HauledTo` nor its caller
`Toils_Haul.DepositHauledThingInContainer` runs (#3242).

`MechanicCardGuards.SiteActions` (#3223) maps a card site to the existing action kind that makes its guards false and
the constraint it acts under; #3285 deleted the per-target guard evaluator and its binding tables, which no live
consumer read. A `SiteActions` row with the constraint `disallow-counted`
(`MechanicCardGuards.DisallowCounted`) is a prevention row: its action disallows the items the site's count guard
counts, which makes the count 0. The one row maps `ThoughtWorker_ApparelThought.CurrentStateInternal` (the
`DeadMansApparel` sites) to `ApparelPolicy`; the tool check requires every guard of such a site to be a count. `thoughtaudit -guards` fails when a table row names a decl or site
absent from the shipped cards or the decompiled sources, or when a stage band names a symbol the sources do not
declare (`GuardTableCheck`, also run over the embedded cards by a test).

`MechanicCardGuards.CardStageOffset` (pure, #3281) prices a count card for one pawn (`CardPawnFacts`: observed trait
and hediff def names, whether Anomaly is active) and one outfit (its `GearOption` items). Each item is counted when the
`where` predicate holds through the `ItemBindings` table (`Apparel.WornByCorpse` is `GearOption.Tainted`, native
`NativeGearFacts`); `over` must be a `CountCollections` row (`Pawn_ApparelTracker.WornApparel`). The one site whose
count condition holds gives the stage, clamped to the last defined stage as native `ThoughtState.StageIndexFor` does,
and the offset is that stage's `baseMoodEffect` (`DeadMansApparel`: -5, -8, -11, then -14 for four or more). It is 0
when a nullifying trait or hediff is present or no site holds (a count of 0), and unknown when a binding, an
observation or a modelled guard is missing. A hediff only Anomaly adds (`AnomalyHediffs`: `Inhumanized`) is absent
while Anomaly is inactive, even when the hediff list is unread. It does not check `Live`; only a verified card may
drive play. The effect-source prevention branch consumes it. `MechanicCardGuards.Counts` says
whether a card counts one item (some site's `where` holds), unknown when a binding is missing.

A card is `verified` only for the code it was probed against. Its drift hash is the first 16 hex digits of SHA-256
over the `bodyHash` of every site, every count guard's `via` override and every writer of the draft cards (follows included), distinct, sorted ordinally and joined by
newlines; any change to one of those methods changes it. The hand-appended
`brain/src/RimGovernor.Brain.Policy/mechanic_cards_verified.tsv` (`def`, `drift_hash`, `probe`, `game`, `evidence`;
not embedded) records each observed probe pass. `thoughtaudit -guards` prints every live card's drift hash and ships
the card `verified` when a row matches def and hash. Otherwise the card ships `extracted` and the tool prints the
def's stale rows (a game update changed a method), so the probe is re-run and a new row appended. A row whose def has
no card, or whose card is `unreachable`, fails the tool. To verify a card: run its probe case once, append the row with the printed drift hash and the
run evidence, and rerun `thoughtaudit -guards`.

The probe for `KnowBuriedInSarcophagus` is `burial/stranger-sarcophagus`. It records the observed vanilla fact: a
colonist's native haul of a stranger corpse into a fresh sarcophagus gives no colonist the memory and leaves the
corpse's `everBuriedInSarcophagus` false, because `Notify_HauledTo` returns unless `thisIsFirstBodyEver`, which only
`TryAcceptThing` sets and no haul path calls (#3242).

The probe for `DeadMansApparel` is `apparel/dead_mans` (#3283). The fixture op `test/deadmans_wear` dresses the first
colonist in five fresh, compatible garments, marks the first n `WornByCorpse` and adds or removes `Bloodlust`. For each
step the case reads the pawn's situational thought rows once the cache is clean, lifts them as the brain's mood thoughts
do (`RoundsMoodReads.MoodThoughts`), and compares the thought's offset with `CardStageOffset` for the same pawn and
outfit: none for n = 0, -5/-8/-11/-14 for n = 1-4 and none with Bloodlust at n = 2. For n = 5 it requires the thought
and records the observed stage against the card's clamp. With Bloodlust the situational row stays in the cache at
offset 0 (`ThoughtUtility.ThoughtNullified` zeroes `MoodOffset`), so the lifted mood thoughts omit it. It is a typed case, whose waits run the game through RunAt, so it
declares no `VanillaColonists` opt-out.

The probes for `EnvironmentCold` and `EnvironmentHot` are `environment/cold` and `environment/hot` (#3328), typed
cases on a one-colonist lab. The fixture op `test/environment_temperature` seals the colonist in a doorless roofed 3x3 room and
moves the lab's outdoor pin and the room to a temperature `deficit` degrees past the pawn's own comfy bound (mid-band
for 0), so wall equalization cannot drift it; under Ideology it adds or strips `Temperature_Tough` on the colony
ideoligion. Each step reads the thought as the `DeadMansApparel` probe does and takes the deficit from the same read's
body stats (`cell_temperature_c` against `comfortable_min_c`/`max_c`), which must sit within 1 degree of the asked one,
then compares the offset with the card's stage band: none in the band, -4/-8/-12/-16 at deficits 5/15/25/35, none at 15
with `Temperature_Tough`. Without Ideology the Tough step is skipped and reported as not observed.

`EffectSourcesRules.GenerateEffectSources` (pure, #3224; prevention branch #3284; gear warmth #3329) turns the verified
prevention and temperature cards into per-pawn gear candidates. A prevention card matches a pawn when it is `Live`,
every site maps to a `disallow-counted` `SiteActions` row, its count guards count one bound decl, and `CardStageOffset`
for the pawn's worn apparel (its `GearLoadoutInput.Worn`, primary weapon excluded) is known. For each pawn the gear
planner predicts an outfit per (tainted filter, insulation floor) pair: tainted disallowed, plus allowed when a prevention
card is Live and a worn or physical option is tainted (the `disallowed` predicate drops matching worn items unless
locked); per filter, the worn insulation floor, plus, when a temperature card is Live, the distinct insulation levels of
the warmest, coolest and unfloored outfits over physical options, at most 3 (`effect_source_floors_truncated`). Each is
one labor-free `ForecastCommitment` `gear/<pawn>/<allow-tainted|disallow-tainted>/cold<c>-heat<h>` with
`ExclusiveOutcome` `gear/<pawn>`, so a pawn's pairs never combine. It carries each card's offset for the predicted outfit
minus the worn one as a `ForecastSituationalChange`, and the summed quality-scaled insulation change as a
`ForecastComfortShift` (minimum by minus the cold change, maximum by the heat change). A pair is offered only when a Live
card prices it: a situational change, or a comfort shift on a side a Live `EnvironmentCold`/`EnvironmentHot` card reads. An
armor-only alternative (#3569: the planned apparel differs from the worn in groups, layers, sharp, blunt or heat rating, quality or
condition) is also a candidate, carrying the outfit change alone; whether it is chosen is the value comparison's call (harm
avoided against cost), not generation's.
Candidates are in pawn order; a budget reports `effect_source_generation_truncated`. An unknown offset or counted item
gives a finding and no candidate. The comparator, not the generator, decides whether the mood change is worth the warmth.

##### Mechanic cards: now live

Since #3285 (epic #3217 Gate 1) the cards drive play. `GenerateCoupledActivities` appends the effect-source family
next to the clothing family, behind the same `military/equipment` Need enablement and gear census
(`EffectSourceFactsOf`: every unblocked pawn with a loadout model, under its climate, its trait def names from the
work facts and its hediff def names from the medical read's `CareConditions`; an unread list leaves the offset unknown,
so the pawn gets `effect_source_offset_unknown` and no candidate). After the review, `GearChoices` reads the
comparator's outcome per pawn: a selected pair gives its filter and floor; a rejected assessed pair that removed a
tainted item keeps the worn outfit (tainted allowed, worn floor); absent or never assessed keeps the default (tainted
disallowed, worn insulation floor). The Rounder keeps that outcome in derived memory for the review's world (a review
with no coupled selection leaves it standing) and the rounds census read stamps it onto `GearPawn.AllowTainted`,
`ColdFloor` and `HeatFloor` for every gear reader, which drives the outfit filter and the planner's floor ([tainted apparel](equipment-upkeep.md#tainted-apparel-and-warmth)). Nothing else decides tainted apparel or warmth: the
gear planner's thermal scalar (#3329), its hand mood curve, its constraint-mode mood tier, the `TaintFree` trait flag and the always-exclude rule
are deleted. What came from the card: the stage offsets, the count predicate (`Apparel.WornByCorpse`) and the
nullifying traits and hediffs. What came from generic machinery: the gear planner's outfit prediction, the
comparator's break-risk and cold-exposure ranking and the apparel policy write.

### Mood ledger

`policy.BuildMoodLedger` (pure) turns pawns' thought rows (`MoodLedgerPawn`: the
`MoodThought` rows plus traits, precepts and expectation level as facts) and a
`map[def]ThoughtFacts` into:

- per pawn, the mood lost per thought: negative offsets only, most negative first, after
  dropping thoughts the pawn cannot carry (a nullifying trait or precept, a missing
  required trait, an expectation below `MinExpectation`); unknown when the pawn's thoughts
  are unreadable;
- per colony thought, `Pawns` affected and summed `Lost`, ranked most negative first,
  with the audit owners;
- `Unowned`, the same ranking restricted to thoughts with no owner;
- the expectation levels by pawn count, shown only (no concern fixes it).

An unknown attribute or a def without facts never drops or zeroes a loss: the observed
row counts and the source's `Unverified` count rises.

Every Round builds the ledger in `RoundsMood.cs` and publishes it as
`RoundsFacts.MoodLedger` (unknown whenever the mood census is). Inputs: the census thought
rows, `AllThoughtFacts()` from the load's catalog (none without a catalog, so every source
is unverified), each pawn's biography traits and the ideology precepts from its policy
inputs. A pawn whose biography or policy block was skipped has unknown traits or precepts.
The expectation level is the native `PawnSocial.expectation_def_name` (the pawn's current
`ExpectationDef`); a pawn whose social block was skipped or carried none has an unknown
level, so its `MinExpectation` thoughts stay unverified and it is left out of the ledger's
expectation list. Later children
(display, the colony aggregate, the gathering trigger) read this fact; the ledger itself
changes no relief or provisioning decision.

The colony status route (`/api/player/colony`) carries the held fact as `moodLedger`
(null until a review has filed): `sources` ranked by mood lost, the `unowned` subset
(empty `owners`), each source's `unverified` count, `unknownPawns` and the `expectation`
levels (empty while the level is unobserved). `RimGovernor.cmd colonyreview` copies it into each
frame and the hourly panel renders the two tables, saying "Expectation level not
observed" rather than assuming one. Unowned thoughts ranked by mood lost choose the next gap-fill.

## Supported need relief

Food, rest and recreation use exact candidates in the shared portfolio. Selected
targets, timing and quantities pass through the existing Method journal and Hands.
The former MoodRelief action and native need-giver wire arm are deleted; a mood
Need cannot dispatch a second local need choice. Preventative psycast relief reports unsupported_forecast_mood_cast until caster occupancy, psyfocus and mood outcomes have shared alternatives. Method admission rejects an Ability under a `people/mood/<pawn>` Need; active mental-state containment and combat retain their existing protection ownership.

An accepted activity is not recovered mood or completed ingestion. Fresh native
observations and exact assignment identity govern completion, interrupted needs,
release and reconsideration. Unknown inputs do not certify recovery. See
[pawn activities](pawn-activities.md) and [voluntary control](voluntary-control.md).
## Mental breaks and containment

An active mental break keeps its pawn's mood Need unmet and releases its
reserved work through shared cancellation. State kind, aggression and age remain
mood evidence. Non-violent breaks leave the clock running.

SocialFighting leaves the clock running despite its native aggression flag:
it neither makes `military/combat` unmet nor selects a subdue response. Independent hostile
threats and urgent medical needs keep their emergency classification.

Other standing aggressive colonists activate defense. The brain drafts one or two
nearest healthy responders, each armed with a melee weapon or unarmed (fists, as
the game's own subdue), and applies an AttackMelee `GiveJobIntent`; the
undraft sweep releases them afterward. Other dispatch stays outside an eight-cell
radius of the target; uncertain attempts remain reconcilable. Downing the target ends
containment and lets the ordinary RESCUE planner carry the pawn to a colonist bed
using native bed selection. There is no Capture or prisoner custody; Arrest remains a
non-aggressive custody operation.
A ranged-equipped colonist never responds: shooting a colonist is not a subdue.
Against a breaker wielding a melee weapon, or whose weapon is unknown, a fists-only
squad is refused and the squad leads with the nearest melee-armed responder.
`BreakResponseTests` pins the admitted responder set.

Containment clearance applies to new dispatch's observed worker and explicit target
positions (including the fixture's nearby damaged wall), not a predicted walking
route. The case audits ordered native dispatch requests against pawn censuses
throughout containment while repair stays enabled. SUBDUE and its plan-owned drafts
are exempt; the undraft sweep remains available. A receipt or completed plan alone
cannot satisfy the native bed outcome.

### Native subdual

The AttackMelee `GiveJobIntent` accepts exact colonist snapshots and a spawned target
pawn; which target is worth subduing (an aggressive, standing colonist) is policy. It drafts an undrafted responder (the plan's draft keeps it
drafted) and issues an ordinary AttackMelee job; an existing draft is retained.
Any violence-capable responder is legal whatever it wields. The job prefers a
legal blunt verb, including fists, without changing native damage. It ends when the
target is downed or its aggressive break ends. Death is failure, never successful
containment. Progress requires the same order on a still-drafted responder. The
mood/subdue case covers living containment.

## Recreation

`rooms/comfort` supplies a reachable recreation source; the comfort Needs then add a second distinct building-backed joy kind for multiple colonists, or for a
lone colonist bored with their only reachable kind. The native census supplies kinds,
each colonist's tolerance and boredom, and available building methods. Selection
prefers a researched, powered television, then billiards, chess or horseshoes when
they add a kind. Two kinds cap this provisioning; inaccessible existing facilities
are access blockers, not reasons to duplicate them.

After Brewing research, ordinary resource production targets twelve Beer and twelve
SmokeleafJoint, with small hops and smokeleaf plots when climate permits. Each
colonist's existing drug policy is observed for inventory/runway demand; the work
routine does not create a policy or select prevention, maintenance or weaning doses.
The [`drug_policy` action](action-contracts.md) remains an explicit native operation.
Scheduled drugs, active dependencies and unsupported chemical needs remain visible
unsupported control states; this does not establish successful addiction treatment
or safe autonomous drug use. Shared recreation currently excludes ingestible joy.

## Gatherings

The `gathering` action (`GatheringIntent`: `gathering_def` plus a required
`organizer` pawn) starts one vanilla `GatheringDef` party; Ideology precept
gatherings stay under `ritual begin`. Any `GatheringDef` is accepted on the wire.
The intent carries no spot: `GatheringWorker.TryExecute(map, organizer)` takes none
and the game chooses it. Game code read with ilspycmd:

- Native validates with `GatheringDef.CanExecute(map, organizer, ignoreGameConditions)`
  and refuses otherwise. `AcceptableGameConditionsToStartGathering` needs local hour
  4 to 21, danger rating None, no lord job that forbids new gatherings, at least four
  spawned free colonists, none bleeding, under half drafted, and enough potential
  guests (`round(0.65 x colonists)` clamped to 2..10 who may join joy and are awake,
  fed, not bleeding or in a mental state). The worker also needs a spot.
- `GatheringsUtility.PawnCanStartOrContinueGathering(organizer)` must hold: not
  drafted, bleed rate at most 0.3, not a prisoner or slave, blood loss at most 0.2,
  not a wild man, inhumanized or subhuman, spawned, not downed, no mental state.
  Native refuses otherwise.
- Spot choice (`GatheringWorker_Party.TryFindGatherSpot` ->
  `RCellFinder.TryFindGatheringSpot`) prefers a built, colony-owned building of any
  def in `GatheringDef.gatherSpotDefs` (`PartySpot` for `Party`): a random one whose
  cell passes `ValidateGatheringSpot` (standable, not dangerous, roofed unless outdoor
  joy is enjoyable, not forbidden, reservable and reachable by the organizer, enough
  reachable guests). Only when none validates does it fall back to cells within 4 of
  an active `CompGatherSpot` and then within 25 of the organizer, rejecting rooms
  with under 10 cells that are not huge or outdoors. So a PartySpot controls the
  spot; policy keeps one placed (below).
- The party area is the whole room when the spot's room is at most 100 cells, not
  huge and not outdoors, otherwise cells within 18.

### Common-room targets

`policy.CommonRoomTargets` sets the impressiveness target of each dining and rec
room (no beds) to the tech-tier baseline (`ImpressivenessLevels.Baseline`), the same
for any colonist count.

### The PartySpot

`rooms/party-spot` keeps exactly one free, instant-build `PartySpot` placed, independent
of any gathering. `policy.ReviewPartySpot` scores the dining and rec rooms
lexicographically: the highest observed impressiveness stage (the Impressiveness
`RoomStatDef` score stages, `ImpressivenessLevels.Stage`), ties broken by cell count.
It owes one step, each resolved by a single action through the existing paths:

- no spot stands: a `building` action places `PartySpot` on a free, roofed, indoor cell
  of the winning room (the game's `ValidateGatheringSpot` filter), or on any such cell
  while no shared room qualifies;
- a room of a strictly higher stage than the incumbent's room exists (the incumbent's
  room is read from the observed spot, never stored): the old spot is deconstructed
  with the `deconstruction` action and the new one is placed on a later pass; rooms of
  the same stage never move it;
- duplicates: each extra spot is deconstructed.

The Need is `rooms/party-spot`, Unmet while `RoomsFacts.PartySpotOwed` holds; it waits on
`rooms/hosting`, which waits on `rooms/comfort`, so the step runs only once both facility
Needs are met, and its own building planner places or retires the spot. Guards keep it
from staying Unmet: a blueprint or frame already
standing, an unbuildable `PartySpot` (definition unavailable or no builder), no free
cell, or an already spent per-Episode one-shot (`retireBuilding` once per building, one
placement per room, read from the Need's own Methods) all drop the deficit. Mood relief
for `AteWithoutTable` and `NeedJoy` defers to `rooms/comfort` while it is active.

### Gathering ownership

`rooms/gatherings` is Unmet while observed mood demand owes a party (`policy.PlanGathering`,
whose cooldown is the AttendedParty memory, with no stored history), after `rooms/party-spot`.
Ordinary automatic participation is unsupported: the family holds the ready Need with
`unsupported_forecast_gathering`, and Standard Method admission rejects a direct
`gathering` action. Starting a lord cannot bypass shared participant availability,
need timing and displaced work. Gathering support requires those alternatives in
the existing shared forecast and Method path.

The independent native operation still checks the exact `GatheringDef`, organizer,
`PawnCanStartOrContinueGathering`, game conditions and `CanExecute`, then calls
`GatheringWorker.TryExecute`. Applied means a lord was created, not that guests attended. Repeating the
operation while that gathering already runs reports the existing lord. Native
participation outside Governor control remains game-owned; this is not evidence
of planner-owned coordination. Ideology rituals use their distinct native operation
and have the same unsupported ordinary participation boundary.

### Where the margins, ledger and party are proven

The relief margin and the ledger's owner routing are policy, held by xUnit:
`PeopleFamilyTests` (the one 0.15 threshold above the minor-break line, and the unmet
`people/mood/<pawn>` Need it gives) and `MoodLedgerTests` (thoughts routed by owner and
ranked by mood lost; an unowned thought alone in `unowned`). `RoomsFamilyTests` holds that an
owed party stays an unsupported held Method. The nightly `mood/gathering` acceptance case owns
only the native half: an explicit `GatheringIntent` starts a Party whose lord the named
organizer joins, read as `PawnState.lordJobClass`. It is not evidence of planner-owned
attendance.

## Observations

**Active mental state.** Every pawn row carries `mentalState`, a `PawnMentalState`
of the native defName, `isAggro` (`MentalStateDef.IsAggro`) and `ticks`
(`MentalState.Age`, 30-tick granularity). It describes the active state, not a
prediction from mood, and is absent exactly when no state is active. The brain
projects it as `EmergencyPawn.MentalState`, a `Fact[policy.MentalState]` that is
known only while a state is active. Worker eligibility uses mental-state presence.
`pawn/mental-state` stages Berserk on a colonist through `test/stage` (`StagePawn.MentalState`)
and verifies both reads.

**Inspiration.** Pawn rows carry `inspiration`, the current `InspirationDef`
defName. An empty string is a known "no inspiration"; the field is absent when the
pawn has no inspiration handler, and the brain keeps that unknown. It reaches
`policy.PawnProfile.Inspiration` through `WorkPawn.Inspiration`; nothing acts on it.
`pawn/inspiration` stages `Inspired_Creativity` through `test/stage` (`StagePawn.Inspiration`)
and reads it back through the profile.
