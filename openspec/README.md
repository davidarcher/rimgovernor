# Requirements with OpenSpec

[Documentation](../docs/README.md) · [Product requirements](../docs/requirements/README.md)
· [CLI setup](../docs/developers/agent-runbook.md#openspec)

Food security is the pilot. Its [standing specification](specs/food-security/spec.md)
owns RG-FOOD-001–013, based on the maintainer-confirmed policy in the
[decision record](changes/redefine-food-requirements/proposal.md#decision-provenance-and-review-boundary).
The [evidence page](../docs/requirements/food-security.md) retains historical source
and test inspection, results and proof limits; it does not certify the expanded
requirements. Other standing capabilities remain in the existing collection.
The active [change](changes/redefine-food-requirements/proposal.md) also captures
related acquisition, hunting, growing and herd drafts. Design and implementation
tasks are outside this requirements capture; the change remains active.

## Authority

Documented intent, inferred candidate and human-approved intent remain distinct.
Migrating or synchronizing text does not approve it. The food policy has explicit
maintainer confirmation; its concrete scenarios illustrate that direction rather
than establish separately approved tactics. To adopt or change intent, record the
maintainer's explicit decision and its source. Do not copy inferred candidates
into the standing specification as agreed requirements. Preserve stable RG IDs
when moving or changing a requirement; never reuse a retired ID.

Developer contracts own subsystem behavior, typed boundaries and state ownership;
they do not duplicate the player outcome requirements. AGENTS.md owns the working
agreement. GitHub owns actionable deferred work; the review queue owns evidence
investments and unresolved source disagreements.

## Changing food behavior

Use the installed propose and apply workflows described in the runbook. In a
food change:

1. Identify the affected RG IDs, intent provenance and observable outcome in
   the proposal. Put additions or modifications under
   `changes/<change>/specs/food-security/spec.md`; include the complete requirement
   block for a modification and retain its stable ID in the heading.
2. Trace design through native facts, pure policy, shared demand and Method
   admission, Hands, native orders and observed outcomes. Identify which
   developer contracts need updating.
3. Include decision tests and native assertions appropriate to the claim in
   tasks. Update the evidence page alongside the implementation. Keep inspected,
   registered, executed and passed distinct and retain run provenance.
4. Validate OpenSpec and run the repo check from the worktree root:

   ```powershell
   openspec validate --all --strict --no-interactive
   .\RimGovernor.cmd test > test.out 2>&1
   ```

5. Synchronize the reviewed delta into the standing spec and archive the completed
   change using the installed workflows. Include the resulting spec, evidence,
   contracts and archive in the milestone committed and landed through
   `RimGovernor.cmd land`. A docs-only migration establishes no new gameplay
   result. Required native verification remains subject to the existing issue
   closure and evidence rules.

OpenSpec validates document structure; it does not prove the governor implements
a scenario. A task checkbox or archived change is not native execution evidence.
The food baseline can be inspected without creating a change:

```powershell
openspec list --specs --json
openspec show food-security --type spec --json
```

The pilot is useful if a reviewer can find each obligation once, distinguish its
intent from implementation and proof, and review a behavior delta without losing
those distinctions. Expansion to other capabilities requires the maintainer's
decision after reviewing this pilot.
