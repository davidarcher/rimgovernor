# Research tied to colony needs

[Documentation](../../README.md)

The `economy/research` Need (#3263; was EnsureResearch) shares ColonyPlan, priority arbitration and Hands. Advisory concerns cannot create
research orders.

Routine research runs through the selected native project, available benches and
numeric work priorities/timetable. Detailed one-hour research slices remain
conditional investment estimates; selecting one creates no `PawnActivities`
order and reserves no pawn or bench. Actual research progress is observed from
the native census. A future bounded intervention requires its own concrete
justification; forecast value alone does not supply it.

Every routine frame carries its research census. Decoding a frame without that
section fails the read. The review retains a total `ReviewFacts.Research` record;
an observed empty finished-project list is valid, while a synthetic projection
that never supplied the census throws before research-dependent planning.
The census retains its actual native snapshot, listed project rows and knowledge
slots. Static definition facts stay in the catalog. A catalog project absent from
a filtered snapshot has an explicit `NotListed` observation and reads completion
from that snapshot's total finished set; it cannot imply a startable project.
Review summaries compute native values from the retained census.

## Target

`policy.ResearchConcern` picks the first unfinished project among the recorded needs, all *derived*
(the concern stays in deficit while the project is current):

1. The project a maintained production target's workshop ladder recorded as gating its bench.
2. The fishing request and ComplexFurniture, from the census.
3. Every project a planner reported waiting on (`awaiting_plan:research:<project>`): a planner that
   cannot proceed for want of research is the evidence that the research is owed (`ColonyReview.NoteResearchWait`).

There is no fixed roadmap: with no need recorded nothing is researched and the concern is disabled. The
planner lends the clock ticks until the target finishes, and skips projects the installed game does not list.

A concern whose only method is gated on research reports the project instead of no method:
the `power/basic` Need with every generator definition unavailable and the `layout/stone-shell` Need with no
replacement material report `awaiting_plan:research:<project>` (`policy.ResearchGate`).

## Construction technology

The construction TechTier is derived from the finished research census with the
player faction's native tech level as its floor. Both direct colony reads and
routine frames derive it from their total Tables.FinishedResearch census. Status reads its research
census directly, so it does not depend on a prior routine review. An Undefined faction level fails the read; it never implies Camp.
An observed empty census with a Neolithic faction is Camp.

## Anomaly knowledge

A knowledge-category project is selected through the same `ResearchIntent` into its category's slot
(`ResearchManager.SetCurrentProject` picks the slot by `knowledgeCategory`) and progresses from study
knowledge, not research work: no researcher, no clock ticks and no bench unless it names one.

- The research read lists every unfinished knowledge project with its lock reasons (`hidden` while the
  entity codex hides it) and every category's slot (`ResearchSnapshot.Slots`); only the ordinary
  slot's project is the current project.
- An empty slot with a startable project of its own category is an `economy/research` deficit (knowledge
  arriving for a category with no project is lost). The step fills it, before the ordinary slot is
  judged, with the head of the concern's prerequisite queue when that head is a knowledge project, else
  the cheapest startable project of that category (`policy.KnowledgePick`: apparent cost, then name).
  A filled slot is never replaced; a disabled concern funds no slot.
- Categories never substitute: Advanced knowledge overflows into Basic, never the reverse
  (`ResearchManager.ApplyKnowledge`).

## Prerequisite graph and selection

The installed prerequisite graph, costs, tabs, techprints and required benches are the
`ResearchProjectDef` rows of the definition catalog; `Observations.ReadResearch` supplies progress (the
researched fraction of the cost, 1 for a zero-cost project), lock reasons and finished projects only; its
progress read lists every current, knowledge or started project, so an unlisted unfinished project has none.
A project outside the definition catalog fails the read.

- Ordinary and hidden prerequisites both participate in traversal. Missing and hidden (entity-codex)
  projects block research; knowledge-category projects do not.
- Traversal visits at most 128 unfinished nodes and keeps at most eight queued projects and eight
  capability inspections per review. Completed prerequisites are omitted; stable native definition
  order breaks ties.
- Vanilla pawn priorities schedule research. Repeated reviews never switch an active project
  to a newly preferred one. Obsolete
  requests leave the queue without clearing already-issued native research.

**Ownership.** The current project is player-owned unless this load's brain recorded its verified
selection. A cleared project, another project, changed player direction or changed load prevents
taking ownership. Autonomous selection uses `expectedCurrent` (empty string = empty ordinary slot); the
native main-thread write checks that value and the exact paused colony/load/map again. The runtime
rejects obsolete concerns and invalidated preparation before dispatch, and durable Hands receipts prevent
automatic replay of uncertain writes.

## Laboratory

Research work coverage uses the deterministic work allocator, including disabled-work checks. A usable
laboratory is powered (or needs no power), matches the project's native required bench, and has every
required facility active on that same bench. Missing capacity, staff, techprints or other native
conditions produce a blocker with the laboratory requirements. Laboratory construction and power use
the shared development methods; dependent construction and production keep native material, placement
and recipe preflight.

When the next rung's only native lock is the research bench (`lock_reasons` holds just
`research_building_or_facilities`), the research Need walks the facility ladder instead of selecting: it
stages a `SimpleResearchBench` in a room whose native role hosts the Laboratory facility (a Workshop,
Barracks or plain room; the starter shell), else a starter shell first, through native placement and
material admission. Selection follows once the bench stands. Holds: `research_bench_needed` (no
placement-capable source), `research_bench_unavailable` (the census lists the bench definition
unbuildable). Power requirements keep explicit blockers until their
methods provide them.

### High-tech bench and analyzer

The planned laboratory (`PlannedLab`, an 11x6 interior) holds two hi-tech-width (5x2) benches in one back-wall
row plus one reserved analyzer slot (2x2) on the room's centre line, clear of every worker cell and within the
analyzer's link distance (8 cells) of each bench. `policy.RoomFurniture.AdvancedLab` and `Analyzer` are
derived from the catalog (the other laboratory bench that links a research-speed facility, and that
facility), never named; both are optional. The research Need builds them ahead of need: once the census lists
the hi-tech bench buildable, it builds one in the planned laboratory,
then the analyzer once the `MultiAnalyzer` project is done. Only an admitted building plan preempts the
research step; every other outcome of the build-ahead (nothing owed, no space, a funding or placement hold)
falls through to the ordinary selection or wait, so research is never blocked behind it. An owed buildable
advanced building also makes the colony want the laboratory (`coreRoomsWanted`). The default ladder walks
`MicroelectronicsBasics` then `MultiAnalyzer` after the medieval crafts, inside the Stable stage's rungs.

## Completion

Selecting a project completes only the selection action. The concern observes native points, finished
projects and the capability's research readiness. No-progress holds use game ticks; renewed native
progress can release the hold. Construction methods blocked on research resume only after fresh
definition availability. A selection receipt, a completed prerequisite, or a zero-length queue with an
unresolved capability cannot certify the requested unlock.

## Acceptance

No native case proves a derived research need: `production/ladder` stages Fabrication finished. A colony
snapshot replay covers
the default ladder and its bench: with Stonecutting at 97% and no bench the research step selects
Stonecutting and owes a bench, the bench step admits an indoor research bench, and with Stonecutting
finished Electricity is next. Advanced facility installation, all research projects and sustained
colony development remain uncertified.
