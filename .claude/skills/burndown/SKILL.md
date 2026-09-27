---
name: burndown
description: Review open RimGovernor issues, triage them, and burn down the ones that are ready
argument-hint: "[label or focus area, optional]"
---
Review and burn down open issues in davidarcher/rimgovernor. Focus: $ARGUMENTS (all open issues if empty).

## Ask me, don't guess
Stop and ask me with AskUserQuestion (options and a recommendation, not an open question) whenever:
- an issue's intent or acceptance is unclear;
- a fix needs a large design decision, or it's a one-way door: save or wire formats, the GABP contract, public action kinds, deleting a subsystem, or anything expensive to change later;
- two issues conflict, or closing one as won't-fix/duplicate is a judgement call.
Subagents can't reach me, so they stop and report the question to you instead. Relay it to me, then hand the answer back to a fresh agent. Keep working on everything that isn't blocked while you wait.

## Steps
1. Fast-forward: `git merge --ff-only main`.
2. List open issues (`gh issue list --state open --limit 200 --json number,title,labels,updatedAt`). Read the candidates with `go run ./cmd/issue <n>` from `go/`.
3. Triage each one as: **ready** (clear acceptance, small), **stale/done** (already fixed on main; check `git log` and the code), **duplicate**, **needs grooming** (really an epic, so suggest `/groom`), or **blocked on me**.
4. Mini-groom before any work starts. For each issue you plan to take, write a short plan: the approach, the files likely touched, the acceptance, and the order or parallel waves. Show me the triage and the plans as one table. Ask me in one AskUserQuestion batch about everything I need to settle: unclear intent, approach choices, one-way doors, and stale/duplicate closes. Record each answer on its issue so the implementing agent sees it. Close stale/duplicate issues with a one-line comment only after I confirm. Wait for my go-ahead on the plan.
5. Burn down the approved issues: one fresh worktree agent per issue, running in parallel where they don't touch the same areas. Each follows AGENTS.md end to end (branch named for the issue, land `-unverified` rather than waiting on acceptance, Complexity line). Remove each worktree once its agent reports.
6. Finish with a summary: landed, closed, questions still open, and anything newly filed.
