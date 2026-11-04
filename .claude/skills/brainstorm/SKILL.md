---
name: brainstorm
description: Brainstorm a new RimGovernor epic with me and file it as a GitHub epic issue
argument-hint: "<epic idea, a sentence or two>"
---
Brainstorm a new epic with me: $ARGUMENTS

This is a conversation, not an implementation. Don't write code or land anything.

1. Ground it first. Read the relevant code, docs and open or closed issues (`RimGovernor.cmd issue <n>`) so the ideas fit how RimGovernor actually works: Families derive Needs, Methods hold Claims, Hands writes orders, the autopilot has full control. If the idea touches a capability OpenSpec owns (food security, colony labor, habitat), read its standing spec under `openspec/specs/` and say which RG IDs it changes. Read what the idea touches, not the whole repo; a read-only Explore agent (model `sonnet`) is fine for a wide sweep where you want the conclusion, not the files.
2. Play back the problem as you understand it: player value, what exists today, and what's missing.
3. Offer 2-3 distinct approaches in prose per AGENTS.md: check each against the code, say what it changes, costs and gets wrong, and give a recommendation with reasons. Favour deleting or simplifying existing layers over adding new ones.
4. Put every fork that matters to me in the text and wait, especially one-way doors: save or wire formats, the native tool contract, new action kinds, new subsystems, anything expensive to reverse. Don't settle those yourself.
5. Once we agree, draft the epic issue: goal, non-goals, the chosen approach, the key decisions and why, open questions, and a rough split into child pieces (titles only; grooming fills in the details). For an OpenSpec-owned capability, note that the requirements delta goes in an OpenSpec change and the epic will link it. Show me the draft, then create it with `gh issue create --label epic --body-file <file>` only after I say yes.
6. End by giving the next prompt in its own fenced code block: `/opsx:propose` first when a spec owns the capability, otherwise `/groom #<n>`.
