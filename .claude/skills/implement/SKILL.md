---
name: implement
description: Implement a groomed RimGovernor epic by landing its child issues in dependency order
argument-hint: "<epic issue number>"
---
Implement groomed epic #$ARGUMENTS. You are the orchestrator; worktree agents do the work.

1. `git merge --ff-only main`. Read the epic and every open child (`go run ./cmd/issue <n>` from `go/`). If the children lack What/Acceptance/Decisions, stop and suggest `/groom #$ARGUMENTS` instead.
2. Order the children by their "Depends on" links. Show me the plan (waves of parallel issues) in a few lines and start without waiting unless something is ambiguous.
3. For each wave, spawn one fresh worktree Agent per child issue, off current main. Tell each agent to:
   - follow AGENTS.md end to end: branch named for the issue, land `-unverified` rather than waiting on acceptance, Complexity line;
   - treat the issue's Decisions as settled;
   - **stop and report a question instead of guessing** if it hits an unclear requirement, a design decision the issue doesn't settle, or a one-way door (save or wire formats, the GABP contract, new action kinds, shared data models, deleting a subsystem, anything expensive to change later).
4. When an agent reports a question, ask me with AskUserQuestion (options and a recommendation), record the answer on the child issue, then start a **fresh** agent off current main (don't SendMessage the old one). Keep the other agents moving meanwhile.
5. Between waves: `git merge --ff-only main`, remove finished worktrees, and tick the epic checklist.
6. When all children have landed, comment on the epic with a one-paragraph summary (what landed, anything unverified), close it, and report back to me with a Complexity line.
