# Research tied to colony needs

[Documentation](../../README.md)

`EnsureResearch` shares ColonyPlan, priority arbitration and Hands. It is admitted
only when a current construction method records an unavailable native definition,
an accepted building step needs that definition, or a maintained production target
finds a research-locked recipe, or its goal explicitly requests a project. It does not select projects merely because the
research slot is empty. Advisory goals cannot create research orders.

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
construction and power provision use the shared development methods. When no bench
exists and the project accepts a simple laboratory, research stages ordinary indoor
bench construction through native placement and material admission. Advanced bench,
facility and power requirements retain explicit blockers until their methods provide them.
Dependent construction still uses normal native material and placement preflight;
production still uses native recipe availability and persistent resource budgets.

Selecting a project completes only the selection action. The research goal observes
native points, finished projects and the capability's research readiness. No-progress
holds use game ticks; renewed native progress can release the hold. Construction
methods blocked on research resume only after fresh definition availability. A native
selection receipt, a completed prerequisite, or a zero-length queue with an unresolved
capability cannot certify the requested unlock.

The Python regression suite and `research_acceptance.py` scenario this section once
described (native pawn research from zero points in a private Docker worker with a
prepared ordinary research bench, verifying guarded Hands selection, actual
completion and the newly available building definition) were removed with the
rest of the Python acceptance toolchain in
[G01.13](https://github.com/davidarcher/rimgovernor/issues/33); equivalent Go
coverage is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38). Laboratory
construction, advanced facility installation, all research projects and sustained
colony development remain separately uncertified either way.
