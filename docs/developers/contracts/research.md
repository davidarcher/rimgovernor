# Research tied to colony needs

[Documentation](../../README.md)

`EnsureResearch` shares ColonyPlan, priority arbitration and Hands. Its target
(`policy.ResearchConcern`) is, in order: the project a maintained production target's workshop ladder recorded as gating
its bench (a *derived* need: the concern stays in deficit while the project is
current, so the ladder is not left waiting); else the first unfinished rung of
the research ladder (`RoundsPolicy.ResearchLadder`, `policy.DefaultResearchLadder`: Stonecutting, Electricity, Batteries, GeothermalPower, SolarPanels,
Smithing, CarpetMaking, ComplexClothing, Machining, Gunsmithing). A rung is a deficit only while the
research tab is idle: any current project, the player's own included, recovers
it and is never replaced, and the research planner lends the clock ticks until it
finishes. The ladder is only walked under a known research census and skips
rungs the installed game does not list; an empty ladder with no target disables
the concern. Advisory concerns cannot create research orders.

Anomaly knowledge (#1745): a knowledge-category project is selected through the same
`ResearchIntent` into its category's slot (`ResearchManager.SetCurrentProject` picks the
slot by `knowledgeCategory`) and progresses from study knowledge, not research work,
so it needs no researcher, no clock ticks and no bench unless it names one. The
research read lists every unfinished knowledge project with its lock reasons
(`hidden` while the entity codex hides it) and every category's slot
(`ResearchRead.Knowledge`); only the ordinary slot's project is the current project.
An empty slot with a startable project of its own category is a deficit of
`EnsureResearch` (knowledge that arrives for a category with no project is lost):
the step fills it, before the ordinary slot is judged, with the head of the concern's
prerequisite queue when that head is a knowledge project, else with the cheapest
startable project of that category (`policy.KnowledgePick`, apparent cost then
name). A filled slot is never replaced. Categories never substitute for each other:
Advanced knowledge overflows into Basic, never the reverse
(`ResearchManager.ApplyKnowledge`), so a Basic slot never stands in for an Advanced
project. A disabled concern (no target, empty ladder) funds no slot.

A concern whose only method is gated on research reports the project instead of
no method: `EnsureBasicPower` with every generator definition unavailable and
`MaintainStoneShell` with no replacement material report
`awaiting_plan:research:<project>` (`policy.ResearchGate`).

`Observations.ReadResearch` supplies the installed prerequisite graph; `include_unlocks` lists
the definitions each project unlocks and `include_capability` adds research benches,
their facilities and eligible researchers. Unknown or ambiguous definitions remain
unknown. Both ordinary and hidden prerequisites participate in traversal; missing
and hidden (entity-codex) projects block research, knowledge-category projects do
not (below). Traversal visits
at most 128 unfinished nodes and retains at most eight queued projects and eight
capability inspections per review. Completed prerequisites are omitted, and stable
native definition ordering resolves ties. Research uses the shared development
admission limit; an observed owned project retains its slot until native work ends. Repeated reviews do not switch an active
project to a newly preferred one. Obsolete requests leave the queue without clearing
already-issued native research.

The current project is player-owned unless this load's controller recorded its
verified selection. A cleared project, another project, changed player direction or
changed load prevents taking ownership. Autonomous selection uses `expectedCurrent`
(an empty string means an empty ordinary slot); the native main-thread write checks
that value and the exact paused colony/load/map again. The runtime also rejects obsolete concerns and invalidated preparation
before dispatch. Durable Hands receipts prevent automatic replay of uncertain writes.

Research work coverage uses the existing deterministic work allocator, including
disabled-work checks. A usable laboratory must be powered (or
need no power), match the project's native required bench, and have every required
facility active on that same bench. Missing capacity, staff, techprints or other
native conditions produce a blocker with the laboratory requirements. Laboratory
construction and power provision use the shared development methods. When the next
rung's only native lock is the research bench (`lock_reasons` holds just
`research_building_or_facilities`), EnsureResearch walks the facility ladder instead
of selecting: it stages a `SimpleResearchBench` in a room whose native role hosts the
Laboratory facility (a Workshop, Barracks or plain room; the starter shell), else a
starter shell first, through native placement and material admission; the selection
follows once the bench stands (#254). Without a placement-capable source the hold is
reported as `research_bench_needed`; a bench definition the census lists unbuildable
as `research_bench_unavailable`. Advanced bench, facility and power requirements
retain explicit blockers until their methods provide them.
Dependent construction still uses normal native material and placement preflight;
production still uses native recipe availability and persistent resource budgets.

Selecting a project completes only the selection action. The research concern observes
native points, finished projects and the capability's research readiness. No-progress
holds use game ticks; renewed native progress can release the hold. Construction
methods blocked on research resume only after fresh definition availability. A native
selection receipt, a completed prerequisite, or a zero-length queue with an unresolved
capability cannot certify the requested unlock.

Research acceptance: `production/ladder` proves a derived need (Smithing
finished natively and the gated bench built) on the Core tribal baseline. The default
ladder and its bench are a colony snapshot test (#894,
`buildingruntime/rounds_ladder_snapshot_test.go`): the research step's recorded census
with Stonecutting at 97% and no bench selects Stonecutting and owes a bench, the bench
step admits an indoor research bench, and with Stonecutting finished Electricity is
next. Advanced
facility installation, all research projects and sustained colony development
remain separately uncertified either way.
