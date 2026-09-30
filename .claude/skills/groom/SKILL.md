---
name: groom
description: Groom a RimGovernor epic into detailed, implementable child issues
argument-hint: "<epic issue number>"
---
Groom epic #$ARGUMENTS into child issues that an agent can implement without coming back to ask.

1. Read the epic and its existing children: `go run ./cmd/issue $ARGUMENTS` from `go/`, plus any issues it links. Read the code each piece will touch.
2. Find the gaps: vague scope, missing acceptance, undecided design, hidden dependencies, pieces that are too big.
3. Ask me with AskUserQuestion (options and a recommendation) about every unresolved decision, batching related ones. Always ask when a choice is a one-way door or expensive to change later: save or wire formats, the GABP contract, new action kinds, data models other code will build on, deleting or replacing a subsystem. Record each answer in the epic.
4. Propose the breakdown as a table: title, one-line scope, dependencies, size. Each child should land on its own in one agent session. Wait for my OK.
5. Create or update each child with `gh issue create`. Each body has:
   - **What**: the behaviour to implement, concretely.
   - **Where**: the files and packages likely touched, and the existing patterns to follow.
   - **Acceptance**: tests or an acceptance case, and what "done" means.
   - **Decisions**: the calls already made (link the epic), so the implementer doesn't reopen them.
   - **Out of scope** and **Depends on #n**.
6. Update the epic body with a checklist of children in dependency order and a "Groomed" note. End by giving the next prompt in its own fenced code block (`/implement #$ARGUMENTS`) so I can copy it.
