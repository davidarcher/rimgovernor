# Goals family contracts

[Contracts](README.md) · [Persistence](persistence-contracts.md#what-must-survive) · [OTEL signals](otel-signals.md)

The player's goals (`family/goals`, #3489) reach the brain through the goals family (#3494). The family owns
status only and proposes no plans: a goal becomes demand on a channel that already exists, and that channel's own
Need and safety floors decide what happens.

## Needs

Each goal is a Development Need `goals/<kind>/<id>` (`<kind>` the hyphenated `GoalKind`, for example
`research-project`). It is Met, Unmet or Unknown by the kind's predicate over the review facts, with the deficit
in the kind's unit (projects, tiers, items, colonists or animals short; 1 for an adoption).

| Kind | Predicate | Demand | Owning Need |
|---|---|---|---|
| `ResearchProject` | the project is in the finished list (Unknown when the census does not list it) | the project joins the research-wanted set while unfinished | `economy/research` |
| `ResearchTier` | the tech tier reached is at least the named `TechTier` | the first unlocking project of every tier above the one reached | `economy/research` |
| `StockCount` | the economy stock of the thing is at least `Count` | `Count` is a floor in `RoundsFacts.ResourceNeeds` (max-merged with the other floors) | `economy/resource` |
| `Colonists` | the colonist count is at least `Count` | the joiner capacity and lance population target is `max(100, Count)` | `joiners/offers` |
| `Animals` | the colony holds at least `Count` of the race | the herd plan raises the race's band to `Count` and does not retire it | `animals/herd` |
| `Precept`, `Meme` | the ideoligion holds it | none: `NoMethod` `not fluid` on a non-fluid ideoligion, `missing_dlc` without Ideology, otherwise `no_pursuit` | none |
| every other kind | Unknown | none: `NoMethod` `no_pursuit` | none |

A kind with no pursuit is accepted and reported, never refused. A goal's demand is replaced each review, so a
finished or removed goal stops asking. Safety floors are never relaxed: a goal only raises a stock floor, a head
count or a herd band, and the existing planners keep their own refusals.

## Status

`GoalStatus` is `Met`, `Pursuing` (the owning Need is at work or has no block to report), `Blocked` (the owner has
no method and an actionable or held block, or the goal's facts are unread: `facts_unread`) or `NoMethod`. The
family's Method for an unmet goal is only `NeedMethod.Held`, with the subject `pursuing:<owner need>`,
`blocked:<reason>` or `no_method:<reason>`. The owner's state is read from its rounds progress row.

## Telemetry

One `goal_review` decision per goal per review ([OTEL signals](otel-signals.md)): `target` the goal id, `verdict`
the state, `reason` as above, attrs `kind`, `need`, `owner`, `deficit` and the goal's theme progress.

## Toggle

The `goals` family (`RIMGOVERNOR_ROUTINE_FAMILIES`) composes the family and the review's demand injection
(`RoundsCapabilities.Goals`). Off, goals persist and render but ask for nothing.
