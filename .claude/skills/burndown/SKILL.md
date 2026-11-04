---
name: burndown
description: Review open RimGovernor issues, triage them, and burn down the ready ones in this session
argument-hint: "[label or focus area, optional]"
---
Review and burn down open issues in davidarcher/rimgovernor. Focus: $ARGUMENTS (all open issues if empty). You do the fixes yourself, one issue at a time; do not delegate to worker agents (a cold worker re-reads the repo per issue, cannot ask the user, and reports a summary you must re-verify; see `/implement`).

## Ask, don't guess
Stop and ask the user whenever an issue's intent or acceptance is unclear, a fix needs a design decision or crosses a one-way door (save or wire formats, the native tool contract, public action kinds, deleting a subsystem, anything expensive to change later), two issues conflict, or a won't-fix or duplicate close is a judgement call. Ask in prose per AGENTS.md: what each option changes and costs, your recommendation and why, then the question. Keep working on whatever is not blocked.

## Steps
1. Sync: `git fetch origin main && git merge origin/main`.
2. List every open issue, all pages: `gh api --paginate "repos/davidarcher/rimgovernor/issues?state=open&per_page=100" --jq '.[] | select(.pull_request == null) | [.number, .title] | @tsv'` (REST, because cloud sessions block GraphQL and so `gh issue list`). Check the count against `gh api repos/davidarcher/rimgovernor --jq .open_issues_count` and say it in the triage. Triage every issue in focus, not a sample. Read each with `RimGovernor.cmd issue <n>`; read the code only for the ones you plan to take or close. Skip issues another live session owns, and name which.
3. Triage each one as **ready** (clear acceptance, small), **stale/done** (already fixed on main; check `git log` and the code), **duplicate**, **needs grooming** (really an epic; give each `/groom #<n>` in its own fenced code block), or **blocked on the user**. "Needs the game" is not a blocker: run the case yourself with `RimGovernor.cmd acceptance` per AGENTS.md, or dispatch `remote-acceptance.yml` when it needs a clean runner (`gh workflow run remote-acceptance.yml --ref main -f tier=cases -f cases=<list> -f shards=N -f label=<issue>`).
4. Mini-groom before any work starts. For each issue you plan to take, write a short plan: approach, files likely touched, acceptance, and the order. Show the triage and the plans as one table, then put every open decision to the user at once (unclear intent, approach choices, one-way doors, stale and duplicate closes). Record each answer on its issue. Close stale and duplicate issues with a one-line comment only after the user confirms. Wait for the go-ahead on the plan.
5. Burn down the approved issues in order, each as one milestone on this branch: iterate with `RimGovernor.cmd test > test.out 2>&1`; finish with one passing `test`; the orphan sweep across every layer in the same commit; commit with the landing subject and anything unverified in the body; a plain `RimGovernor.cmd land`; then a one-sentence status comment with the commit, and close the issue only when its whole acceptance is met. If the user wants a second issue in flight at the same time, they start a second session for it on a disjoint area.
6. Finish with a summary: landed, closed, questions still open, and anything newly filed.
