---
name: groom
description: Groom a RimGovernor epic into child issues one session can implement in order without coming back to ask
argument-hint: "<epic issue number>"
---
Groom epic #$ARGUMENTS into child issues that `/implement` can land in one session, in dependency order, without coming back to ask.

1. Read the epic and its existing children: `RimGovernor.cmd issue $ARGUMENTS`, plus any issues it links. Read the code each piece will touch: the owning contract, the existing pattern to follow, and the tests that cover it. Read only that; the epic already carries the problem statement.
2. If the epic touches a capability that OpenSpec owns (food security, colony labor, habitat; see `openspec/README.md`), the requirements delta belongs in an OpenSpec change, not in issue text. If the epic does not name one, stop and give `/opsx:propose` as the next prompt so the change exists first; the children then reference its RG IDs and `tasks.md` rather than restating them.
3. Find the gaps: vague scope, missing acceptance, undecided design, hidden dependencies, pieces too big for one milestone.
4. Put every unresolved decision to the user, batching related ones. Ask in prose per AGENTS.md: check each option against the code first, write what it changes, costs and gets wrong, give a recommendation and why, then the question. Always ask when a choice is a one-way door or expensive to change later: save or wire formats, the native tool contract, new action kinds, data models other code will build on, deleting or replacing a subsystem. Record each answer in the epic.
5. Propose the breakdown as a table: title, one-line scope, dependencies, size. Each child is one milestone: one coherent change, one passing `RimGovernor.cmd test`, one `land`. Prefer fewer, well-ordered children over many small parallel ones; a serial order with no shared-file conflicts is what the implementer needs. Wait for the OK.
6. Create or update each child with `gh issue create` (body via `--body-file`). Each body has:
   - **What**: the behaviour to implement, concretely, with the RG IDs it serves where a spec owns them.
   - **Where**: the files and packages likely touched, and the existing pattern to follow. This is the implementer's reading list; name it so they read nothing else.
   - **Acceptance**: tests or an acceptance case, and what "done" means.
   - **Decisions**: the calls already made (link the epic), so the implementer does not reopen them.
   - **Out of scope** and **Depends on #n**.
7. Update the epic body with a checklist of children in dependency order and a "Groomed" note. End by giving the next prompt in its own fenced code block (`/implement #$ARGUMENTS`).
