---
name: implement
description: Implement a groomed RimGovernor epic by landing its child issues in dependency order, one milestone at a time, in this session
argument-hint: "<epic issue number>"
---
Implement groomed epic #$ARGUMENTS in this session. You do the work; do not delegate it.

## Why no worker agents
A worker agent starts cold: it re-reads AGENTS.md, the architecture, the contracts and the issue, so every child pays the warm-up again, and every question it cannot answer costs a round trip through you and another cold start. Its report is a summary you cannot check without reading the diff anyway. The landing lane serializes on remote main regardless, and the orphan sweep only works when one context sees every child. One session, one child at a time, is cheaper and lands cleaner. The only delegation worth its tokens is a read-only Explore agent (model `sonnet`) when a question needs a sweep of many files and you want the conclusion, not the file dumps.

## Steps
1. Sync: `git fetch origin main && git merge origin/main`. Read the epic and every open child with `RimGovernor.cmd issue <n>`. If a child lacks What, Where, Acceptance or Decisions, stop and give `/groom #$ARGUMENTS` as the next prompt in its own fenced code block. If the epic names an OpenSpec change, read its `tasks.md` and `design.md` too; the children and the tasks must agree.
2. Order the children by their "Depends on" links. Show the order in a few lines and start without waiting unless something is ambiguous. Parallel work is the user's call, not yours: if two children are independent and the user wants them in flight together, they start a second session for one of them; you never spawn one.
3. For each child, in order, as one milestone:
   - Read only what the child touches (its Where, the owning contract, the existing pattern it names). Do not re-read the whole epic each time.
   - Treat the child's Decisions as settled. When you hit an unclear requirement, a design decision the issue does not settle, or a one-way door (save or wire formats, the native tool contract, new action kinds, shared data models, deleting a subsystem), stop and ask the user in prose per AGENTS.md: what each option changes and costs, your recommendation and why, then the question. Record the answer on the child issue before continuing. Do everything that does not depend on the answer first.
   - Follow AGENTS.md: iterate with `RimGovernor.cmd test > test.out 2>&1`; finish with one passing `test` on the milestone; the orphan sweep across native reads, wire contracts, brain, tests and docs in the same commit; the commit subject as the landing title and anything unverified in the body; then a plain `RimGovernor.cmd land`. Tick the epic checklist and comment one status sentence with the commit on the child.
4. After the last child, run `RimGovernor.cmd test -full > test.out 2>&1` once. File one issue per failure (the failing area's label, or `area:tooling`); do not fix them in the epic. If the epic has an OpenSpec change, tick its tasks and tell the user `/opsx:archive <change>` is next.
5. Comment on the epic with a one-paragraph summary (what landed, anything unverified, the `-full` result and the issues filed), close it, and report back with the Complexity line.
