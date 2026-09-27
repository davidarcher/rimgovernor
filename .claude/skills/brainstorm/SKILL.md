---
name: brainstorm
description: Brainstorm a new RimGovernor epic with me and file it as a GitHub epic issue
argument-hint: "<epic idea, a sentence or two>"
---
Brainstorm a new epic with me: $ARGUMENTS

This is a conversation, not an implementation. Don't write code or land anything.

1. Ground it first. Read the relevant code, docs and open or closed issues (use `go run ./cmd/issue <n>` from `go/`) so the ideas fit how RimGovernor actually works: planners, goals and methods, the native UI (#951) as the control surface, and the autopilot having full control.
2. Play back the problem as you understand it: player value, what exists today, and what's missing.
3. Offer 2-3 distinct approaches with trade-offs and your recommendation. Favour deleting or simplifying existing layers over adding new ones.
4. Ask me with AskUserQuestion at every fork that matters, especially one-way doors: save or wire formats, the GABP contract, new action kinds, new subsystems, anything expensive to reverse. Don't settle those yourself.
5. Once we agree, draft the epic issue: goal, non-goals, the chosen approach, the key decisions and why, open questions, and a rough split into child pieces (titles only; grooming fills in the details). Show me the draft, then create it with `gh issue create` (label `epic`) only after I say yes.
6. End by suggesting `/groom #<n>`.
