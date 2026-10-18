# Goals and themes

[Player guide](README.md) · [Controls](controls.md)

Goals tell the governor what you want the colony to become. A goal is a condition the colony can reach: a research
project finished, 500 steel in stock, eight colonists, a hospital, a precept adopted. The governor turns each goal into
extra demand on the work it already does and shows you how that is going. A **theme** is a named bundle of goals, such
as "Naked cannibals who worship insects". You set both in a window inside the game; there is nothing to configure in
the launcher.

Goals never override safety. A goal can raise a stock target, a colonist target or a herd size, but the governor keeps
refusing what it would have refused anyway (food, shelter and defence come first). Goals only act while the bot is
on; with it off they are saved and shown but ask for nothing.

## Open the window

Load a colony with the brain running. Click the info icon on the play-settings bar at the bottom right of the map
(tooltip "Open the RimGovernor goals window."). The window has two tabs, **Goals** and **Themes**. It refreshes about
every second and a half and shows "Brain not started." until the brain is up. The icon is absent in headless test
runs.

## Add a goal

On the Goals tab, scroll to **Add a goal** and fill in the form:

| Field | Use |
| --- | --- |
| Kind | What the goal is about (see below). |
| Target | The def name, room role, pawn id or quest id the kind names, for example `Steel`, `Hospital`, `Archogenetics`. |
| Qualifier | A second name where a kind needs one: the stuff of a garment. |
| Count | How many: stock, colonists, animals, beds, wearers. Believers use 0 for every colonist. |
| Deadline | A game day, or blank for none. |
| Importance | Low, Normal or High. |

**Add goal** saves it. A bad entry is refused with the reason under the form. **Edit** and **Remove** sit on each row.

Kinds the governor can work toward today:

| Kind | Means |
| --- | --- |
| Research project, Research tier | Finish a project, or reach a tech tier or tech level. |
| Stock count | Hold a number of a thing. |
| Colonists | Reach a head count. |
| Animals | Hold a number of a race. |
| Room role | Have a room of a role (nursery, playroom, classroom, deathrest chamber, worship room) or a hospital with a number of beds. |
| Worn count | A number of colonists wearing a garment, optionally of one stuff. |
| Precept, Meme | Hold that precept or meme in your ideoligion (needs Ideology and a fluid ideoligion). |
| Believers | A number of colonists who share your ideoligion, by moral-guide and prisoner conversion. |
| Named prisoner | Recruit one prisoner, by pawn id. |
| Named quest | Accept and finish one quest, by quest id. |
| Skill | Raise one colonist's skill to a level: the colonist gets that skill's work and textbooks. |
| Health | Give one colonist a bionic or implant, by its install recipe: the governor gets the part and schedules the surgery. |

The list also offers kinds the governor cannot pursue yet (ritual, territory, ending, caravan, power
surplus; the [goals contracts](../developers/contracts/goals-contracts.md) list exactly which kinds have a pursuit). They save and show with state **No method**. That is deliberate: the window
doubles as a list of what the governor still cannot do.

## Read the state

Each row shows a **State** and, beside it, why.

| State | Meaning |
| --- | --- |
| Met | The condition holds now. |
| Pursuing | The governor is working on it. The detail names the part of the governor doing the work, for example `economy/research`. |
| Blocked | A way exists but something stands in the way right now. The detail is the reason (no spare colonist, development points `3/5`, facts not read yet). It clears when the block does. |
| No method | The governor has no way to pursue this goal here, with the reason: a kind it cannot pursue, a non-fluid ideoligion, a missing DLC or def, no eligible wearer. The goal stays saved. |
| Pending | Added since the last review. |

There is no separate infeasible state and no estimated completion day yet. A goal that would break a safety floor is
simply not pursued past it.

## Importance and deadline

Importance (Low, Normal, High) and a deadline are saved with every goal and shown in the table, which sorts high
importance first. They also weight a theme's progress (High counts four times a Low). They do not yet change what the
governor chooses to do first: valuing goals against everything else the colony needs, and turning a deadline into an
estimated day, wait on the forecast work in
[#3469](https://github.com/davidarcher/rimgovernor/issues/3469) and
[#3470](https://github.com/davidarcher/rimgovernor/issues/3470). Treat both as your record of priority until then.

## Long projects when food is secure

Besides the day ahead, the governor models roughly a game year of the colony: research points, sowing, how long a crop
takes to grow in your climate, and the food in stock. Research and sowing pay off weeks later, so it prices an hour of
them by how much sooner the payoff arrives, in the same terms as an hour of food, rest or idle labor. When food is secure
that hour beats idling, so a researcher at a bench or a grower with a growing zone starts a long project (Devilstrand is
the worked example: 800 research points, then sowing at Plants 10) instead of waiting for a goal to demand it. When
food is short, nothing ranks above eating and long projects wait. The apparel payoff of the Devilstrand chain waits on the
calibrated raid-exposure table ([#3543](https://github.com/davidarcher/rimgovernor/issues/3543)), so that chain does not
start on its own yet.

## Preferences

The Goals tab ends with standing **Preferences**. These are not goals with a finish line; they change how the governor
plays, always:

- **Risk profile** (Cautious, Normal, Bold) and **Pace** (Wellbeing, Balanced, Development).
- **Apparel**: Dressed (default), Nude, or ArmorOnly. The governor stops tailoring clothing nobody will wear.
- **Drug policy**: None, SocialOnly or Medical.
- **Forbidden methods**: human butchery, organ harvest, drugs, slavery, insect meat, human leather. The governor never
  proposes what you forbid.
- **Diet**: Cannibal, Insect meat, Vegetarian.
- **Protected things and pawns** (comma-separated ids): left alone by clearing and demolition, and a protected pawn is
  not drafted while another fighter can fight.

Preferences apply as soon as you change them.

## Themes

The **Themes** tab lists the shipped presets and your themes.

1. Pick **Adopt with importance** and, if you want one, an **Adopt deadline**.
2. Press **Adopt** beside a preset. Each member this game can support becomes a goal, tagged with the theme. A member
   the game cannot support (a missing DLC or def) is listed with the reason and skipped.
3. The theme shows its progress, met over total, weighted by importance. A preset can be adopted once.

A preset's preferences (apparel, diet, risk and so on) are listed as members but **not applied**: set them yourself in
Preferences so they stay yours.

On an adopted theme, each member row offers **Override** (its own importance and deadline), **Remove** (drop that goal
from the theme) and, for a removed member, **Restore**. **Compose a theme from goals** builds your own theme from goals
that belong to no theme: name it, pick importance and deadline, tick the goals, press **Create theme**. A theme's
importance applies to its members unless a member overrides it.

Presets are updated with the mod, and a change to a preset changes the members of themes that follow it.

## Example: naked cannibals who worship monkeys

You need Ideology, an ideoligion that can still be reformed (fluid), and the bot on.

1. Open the goals window and the **Themes** tab. Choose an importance (Normal is fine) and press **Adopt** next to
   "Naked cannibals who worship monkeys".
2. Switch to **Goals**. The theme's goals are grouped under it: the Nudism and Cannibal memes; precepts for nudity
   (male and female), preferred cannibalism and loved insect meat; a Monkey venerated-animal goal; and a Believers
   goal for every colonist.
3. In **Preferences** set **Apparel** to Nude, and tick **Cannibal** and **Insect meat** under **Diet**. This is the
   half of the theme the adopt step leaves to you.
4. Let the bot run. It reforms your ideoligion one step at a time, paid for with development points, so you will see
   **Blocked** with a `development points` reason between steps, then **Met** as memes and precepts land. Believers
   converts colonists and prisoners of other ideoligions through your moral guide's Convert ability; with no role that
   grants it, the goal says why.
5. The Monkey goal is reformed like the precepts: one animal per reform, paid for with development points. RimWorld
   decides which animals may be venerated (never insects or dryads); a goal for one it does not offer reads
   **Blocked** (`venerated_animal_unavailable:<animal>`).

A non-fluid ideoligion shows **No method** `not fluid` on the memes and precepts, and without Ideology they show
`missing_dlc`.

## Related

- [Launcher and controls](controls.md) for Play, Pause and the Observe tab.
- Developers: [goals contracts](../developers/contracts/goals-contracts.md), [goals panel](../developers/architecture/in-game-panel.md) and the [player goals note](../developers/architecture/player-goals.md).
