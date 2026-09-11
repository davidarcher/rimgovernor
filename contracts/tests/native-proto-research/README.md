# Research observation checks

Build `NativeProtoResearch.csproj`, then run its net472 executable with the private
Bridge DLL, Runtime directory, SDK directory, game managed directory and Harmony
directory. Tests load the actual adapter, official messages and SDK argument binder.
They verify bounded requests, dictionary-only progress reads, native completion
math, optional zero/false fields and reply limits. They do not prove gameplay.

ReadResearch defaults to unfinished, visible, startable projects; opt in to locked,
finished, unlocks and map-local bench/researcher capability. Projects sort by exact
definition name. Native selection eligibility considers benches across every map
and ignores bench power, matching ResearchProjectDef.CanStartNow. Capability rows
describe the requested map. Optional absent researcher priority/skill is unknown,
not zero. Hidden projects are excluded. Anomaly inactive means no category slots;
an uninitialized active category list means no selected projects, without saving a
new list. The ordinary slot omits category; an empty slot has no current project.

Complete results require at most256 matching projects and256 entries in each child
collection, with a1MiB reply bound. Oversized reads refuse; no frozen cursor or CAS
snapshot is issued. Native acceptance must compare saved progress/knowledge/slots,
current project, tick and pause state before/after repeated reads, then compare
prerequisite, bench, facility and researcher facts against actual game state.
