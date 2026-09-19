# Research tied to colony needs

[Documentation](../../README.md)

`EnsureResearch` shares ColonyPlan, priority arbitration and Hands. Its target
(`policy.ResearchGoal`) is, in order: the operator's `--routine-research-target`;
the project a maintained production target's workshop ladder recorded as gating
its bench (a *derived* need: the goal stays in deficit while the project is
current, so the ladder is not left waiting); else the first unfinished rung of
the research ladder (`RoutinePolicy.ResearchLadder`, `--routine-research-ladder`,
default Stonecutting, Electricity, Batteries, SolarPanels, Smithing, CarpetMaking,
ComplexClothing, Machining, Gunsmithing). A rung is a deficit only while the
research tab is idle: any current project, the player's own included, recovers
it and is never replaced, and the research planner lends the clock ticks until it
finishes. The ladder is only walked under a known research census and skips
rungs the installed game does not list; an empty ladder with no target disables
the goal. Advisory goals cannot create research orders.

A goal whose only method is gated on research reports the project instead of
no method: `EnsureBasicPower` with every generator definition unavailable and
`MaintainStoneShell` with no replacement material report
`waiting_on_research:<project>` (`policy.ResearchGate`).

`home/research` supplies the installed prerequisite graph. Its optional `capability`
argument resolves an exact `ThingDef:name` or `RecipeDef:name` and returns research
requirements and current availability. Unknown or ambiguous definitions remain
unknown. Both ordinary and hidden prerequisites participate in traversal; missing,
hidden and knowledge-category projects block ordinary research. Traversal visits
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
that value and the exact paused colony/load/map again. The runtime also rejects obsolete goals and invalidated preparation
before dispatch. Durable Hands receipts prevent automatic replay of uncertain writes.

Research work coverage uses the existing deterministic work allocator, including
player overrides and disabled-work checks. A usable laboratory must be powered (or
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

Selecting a project completes only the selection action. The research goal observes
native points, finished projects and the capability's research readiness. No-progress
holds use game ticks; renewed native progress can release the hold. Construction
methods blocked on research resume only after fresh definition availability. A native
selection receipt, a completed prerequisite, or a zero-length queue with an unresolved
capability cannot certify the requested unlock.

Research acceptance: `production/ladder` proves a derived need (Smithing
finished natively and the gated bench built) and `research/ladder`
(`acceptance run research/ladder`) proves the default ladder and its bench, both on the
Core tribal baseline; the research case runs with no target: `test/research_ladder_prepare` seeds
Stonecutting at 97%, a roofed starter hut with sleeping spots (`scripts/fixtures/FixtureHut.cs`), wood and steel beside its door and no bench, and the live research state must show a research
bench the service built, Stonecutting finished and Electricity current. Advanced
facility installation, all research projects and sustained colony development
remain separately uncertified either way.
