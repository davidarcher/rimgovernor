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
1. Fast-forward: `git fetch origin main && git merge --ff-only origin/main`.
2. List every open issue, all pages: `gh api --paginate "repos/davidarcher/rimgovernor/issues?state=open&per_page=100" --jq '.[] | select(.pull_request == null) | [.number, .title] | @tsv'` (REST, because cloud sessions block GraphQL and so `gh issue list`). Check the count against `gh api repos/davidarcher/rimgovernor --jq .open_issues_count` and say it in the triage. Triage every issue, not a sample. Read each one with `go run ./cmd/issue <n>` from `go/`. Skip the issues another live thread or agent already owns, and name which thread owns each one.
3. Triage each one as: **ready** (clear acceptance, small), **stale/done** (already fixed on main; check `git log` and the code), **duplicate**, **needs grooming** (really an epic; give each `/groom #<n>` in its own fenced code block), or **blocked on me**. "Needs the game" is not a blocker: on-demand remote-acceptance CI runs the game (`gh workflow run remote-acceptance.yml --ref main -f tier=cases -f cases=<list> -f shards=N -f label=<issue or agent>`), so an agent reproduces, fixes and re-verifies there. Defer to David's PC only when CI cannot do it, and say why.
4. Mini-groom before any work starts. For each issue you plan to take, write a short plan: the approach, the files likely touched, the acceptance, and the order or parallel waves. Show me the triage and the plans as one table. Ask me in one AskUserQuestion batch about everything I need to settle: unclear intent, approach choices, one-way doors, and stale/duplicate closes. Record each answer on its issue so the implementing agent sees it. Close stale/duplicate issues with a one-line comment only after I confirm. Wait for my go-ahead on the plan.
5. Burn down the approved issues: one fresh worktree agent per issue, running in parallel where they don't touch the same areas. Each follows AGENTS.md end to end (branch named for the issue, run `go run ./cmd/test` once, then `go run ./cmd/land` and push `origin main`, without waiting on acceptance, Complexity line). Remove each worktree once its agent reports.
6. Finish with a summary: landed, closed, questions still open, and anything newly filed.
