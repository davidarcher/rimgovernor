# Research tied to colony needs

[Documentation](../../README.md)

`EnsureResearch` shares ColonyPlan, priority arbitration and Hands. Advisory concerns cannot create
research orders.

## Target

`policy.ResearchConcern` picks, in order:

1. The project a maintained production target's workshop ladder recorded as gating its bench (a
   *derived* need: the concern stays in deficit while the project is current).
2. The first unfinished rung of the research ladder (`RoundsPolicy.ResearchLadder`,
   `policy.DefaultResearchLadder`: Stonecutting, Electricity, Batteries, GeothermalPower, SolarPanels,
   Smithing, CarpetMaking, ComplexClothing, MicroelectronicsBasics, MultiAnalyzer, Machining, Gunsmithing, ChargedShot, BeamWeapons; BeamWeapons is Odyssey's and skipped when the census does not list it).

A rung is a deficit only while the research tab is idle: any current project, the player's included,
recovers the concern and is never replaced, and the planner lends the clock ticks until it finishes. The
ladder is walked only under a known research census and skips rungs the installed game does not list;
an empty ladder with no target disables the concern.

A concern whose only method is gated on research reports the project instead of no method:
`EnsureBasicPower` with every generator definition unavailable and `MaintainStoneShell` with no
replacement material report `awaiting_plan:research:<project>` (`policy.ResearchGate`).

## Anomaly knowledge

A knowledge-category project is selected through the same `ResearchIntent` into its category's slot
(`ResearchManager.SetCurrentProject` picks the slot by `knowledgeCategory`) and progresses from study
knowledge, not research work: no researcher, no clock ticks and no bench unless it names one.

- The research read lists every unfinished knowledge project with its lock reasons (`hidden` while the
  entity codex hides it) and every category's slot (`ResearchRead.Knowledge`); only the ordinary
  slot's project is the current project.
- An empty slot with a startable project of its own category is an `EnsureResearch` deficit (knowledge
  arriving for a category with no project is lost). The step fills it, before the ordinary slot is
  judged, with the head of the concern's prerequisite queue when that head is a knowledge project, else
  the cheapest startable project of that category (`policy.KnowledgePick`: apparent cost, then name).
  A filled slot is never replaced; a disabled concern funds no slot.
- Categories never substitute: Advanced knowledge overflows into Basic, never the reverse
  (`ResearchManager.ApplyKnowledge`).

## Prerequisite graph and selection

`Observations.ReadResearch` supplies the installed prerequisite graph. `include_unlocks` lists the
definitions each project unlocks; `include_capability` adds research benches, their facilities and
eligible researchers. Unknown or ambiguous definitions stay unknown.

- Ordinary and hidden prerequisites both participate in traversal. Missing and hidden (entity-codex)
  projects block research; knowledge-category projects do not.
- Traversal visits at most 128 unfinished nodes and keeps at most eight queued projects and eight
  capability inspections per review. Completed prerequisites are omitted; stable native definition
  order breaks ties.
- Vanilla pawn priorities schedule research. Repeated reviews never switch an active project
  to a newly preferred one. Obsolete
  requests leave the queue without clearing already-issued native research.

**Ownership.** The current project is player-owned unless this load's controller recorded its verified
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
`research_building_or_facilities`), EnsureResearch walks the facility ladder instead of selecting: it
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
facility), never named; both are optional. EnsureResearch builds them ahead of need: once the census lists
the hi-tech bench buildable, it builds one in the planned laboratory (never the shelter's research slot),
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

`production/ladder` proves a derived need (Smithing finished natively and the gated bench built) on the
Core tribal baseline. A colony snapshot test (`buildingruntime/rounds_ladder_snapshot_test.go`) covers
the default ladder and its bench: with Stonecutting at 97% and no bench the research step selects
Stonecutting and owes a bench, the bench step admits an indoor research bench, and with Stonecutting
finished Electricity is next. Advanced facility installation, all research projects and sustained
colony development remain uncertified.
