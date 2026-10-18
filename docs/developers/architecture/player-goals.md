# Player goals

[Architecture](overview.md) · [Goals contracts](../contracts/goals-contracts.md) · [Player guide](../../players/goals.md)

How the player's goals reach the bot and how their status gets back to the player (epic #3478). The player page is
the [goals guide](../../players/goals.md); this note is the shape of the code. Per-kind behavior is in the
[goals contracts](../contracts/goals-contracts.md); nothing here repeats it.

## Records and the blob

`Goals.cs` in `brain/src/RimGovernor.Brain.Policy` holds the typed records: `GoalRecord` (a closed `GoalKind`, a
target and qualifier, a count, importance, deadline, optional theme and overrides), `ThemeRecord` (preset id,
importance, deadline, member goal ids, removed preset-member keys) and `PreferencesRecord`. All validate on save, and
an unknown kind is a save error. The whole set is one colony-scoped blob, `family/goals`, in the native save, with
minted ids that are never reused, so a goal's Need id `goals/<kind>/<id>` is reproducible from the blob. The
[persistence table](../contracts/persistence-contracts.md) owns the row. The kind vocabulary is wider than the
pursuit: a kind with no method still validates, persists and renders as `NoMethod`.

## The goals Family

`GoalsFamily` is a demand injector and a status owner. It proposes no plan and orders nothing.

- Each goal is a Development Need whose predicate is the kind's, over the review facts.
- An unmet goal becomes demand on a channel that already exists. `GoalRules.DemandOf` collects it and
  `GoalRules.Stamp` carries it on `RoundsFacts` into every read of the world, so demand lags a goal by one review.
  The channels are the research-wanted set, the resource floors, the joiner and herd targets, the planned-room
  producers (`GoalBuild`), the outfit requirements and clothing bills, the quest step, the prisoner recruit set, the
  ideoligion reform planner and the conversion Need. The owning planner's own Need, safety floors and refusals decide
  what happens; the family relaxes none of them.
- Status is read back from the owner. `GoalStatus` is Met, Pursuing, Blocked or NoMethod, derived from the owning
  Need's progress row, or from the facts when the kind has no owner. The family's Method for an unmet goal is only
  `NeedMethod.Held`, so a held row explains rather than acts.

[Goals contracts](../contracts/goals-contracts.md) lists each kind's predicate, demand and owner; adding a kind adds a
predicate and a demand, not a Family.

## Preferences are facts

Standing preferences are not Needs. `RoundsFacts.Preferences` is stamped once where the review facts are observed,
and the owning policy reads it: the food policy for diet, the apparel policy for dress, the drug policy planner, the
butchery, organ and slavery refusals, clearance and draft exclusions. There is no veto layer and no Safeguard; the
policy never proposes what the preference forbids ([persistence contracts](../contracts/persistence-contracts.md)).

## Presets by reference

`ThemePresets` (Policy) defines the preset data types and `Bridge/Catalog/ThemePresets.cs` the dozen shipped
presets, built from typed `DefOf` references. A theme stores a preset id and the player's removals; it never copies
the members. `PresetRules.Resolve` takes the preset, the installed `DefinitionCatalog` and the player's per-member
overrides and returns each member with its effective importance and deadline, or marks it unavailable with the missing
def or DLC, reported rather than silent. Adopting mints a goal per available goal member; preference members are
shown and left to the player. A preset change therefore changes live colonies that follow it, and a removed member
named by a saved key simply stops resolving.

## Status to panel

`GoalsRounds.ReadAsync` derives each goal's status and each theme's progress for its `goal_review` decision rows and
publishes them to the in-memory `GoalStatusBoard`, keyed to the colony. `BrainHost.GoalsView.Current()` returns the
record with those statuses, and `BrainHost.GoalsEditor` validates and stores an edit and wakes the `goals` planner.
No HTTP route exists. Theme progress is value-weighted: each goal weighs its effective importance (Low 1, Normal 2,
High 4).

## The panel's threading

The window is a Verse `Window` in the Bridge, drawn on the game thread. It never awaits: `GoalsPanel` caches a pure
view model that a pool-thread refresh swaps in every 1.5 s, and edits queue and apply in order, one save at a time.
[In-game goals panel](in-game-panel.md) has the details, the Themes tab and the install patch.

## Value

Goal Needs are Development Needs and importance and deadline do not enter Need selection. They enter the coupled
selection (#3503): an unmet research or worn-garment goal earns its importance's capability-days for the fraction of the
goal a schedule progresses, decayed per day past its deadline, and a goal whose every route breaks a floor is
`Infeasible` and names it ([goals contracts](../contracts/goals-contracts.md#importance-and-deadline),
[forecast value note](forecast-value.md#goal-value)). The player's risk profile and pace set the forecast's harm weight,
death floor and wellbeing weight. Tier 2 will supply a goal's marginal value and estimated day (#3470, then #3505).
Floors stay floors at every importance.

## Related contracts

[Persistence](../contracts/persistence-contracts.md) (the blob, preferences as facts) ·
[Apparel policy](../contracts/apparel-policy.md#preferences) ·
[Ideology](../contracts/ideology-contracts.md) (reform, conversion) ·
[Action contracts](../contracts/action-contracts.md) (`Conversion`, drug policy) ·
[OTEL signals](../contracts/otel-signals.md) (`goal_review`).
