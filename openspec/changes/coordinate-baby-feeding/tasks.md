# Tasks

Implement [#4040](https://github.com/davidarcher/rimgovernor/issues/4040) against
[the existing food delta](specs/food-security/spec.md) and [the revised design](design.md).
The maintainer authorized vanilla-owned feeding with observation and reactive
coordination. These tasks replace the unstarted exact-input implementation plan;
none is complete merely because its design or native mechanics were inspected.

## 1. Observe feeding and keep unmet demand honest

- [ ] 1.1 Use the canonical baby-care facts to assess hunger/condition, active feeding, current care availability and safe-return state; add only observations needed by those decisions. Verify focused boundary/policy tests distinguish possible care, active care, actual intake and unavailable facts; document the native observation semantics in the owning contract.
- [ ] 1.2 Remove the blanket assumption that a breastfeeding-capable pawn establishes feeding adequacy or always suppresses alternative provision. Verify FoodFamily tests cover unavailable/depleted nursing, working nursing, suitable bottle supply and multiple babies with remaining unmet demand; document provisioning versus actual feeding without creating a second reserve or milk-allocation model.

## 2. Make room for vanilla care and react to outcomes

- [ ] 2.1 Connect the Food response to shared Method admission/release so justified childcare can obtain a caregiver held by competing governor work. Verify tests through the real release/admission path preserve revisions, partial work, carried inputs and dependent participants, change nothing for a losing response and respect protected actors. Update voluntary-control and continuation contracts.
- [ ] 2.2 Let vanilla choose and execute legal feeding and associated safe return, protecting the active caregiver and baby from conflicting assignments until the native care lifecycle is finished. Verify integration tests reject premature reassignment and allow the associated cleanup without authorizing unrelated jobs. Reuse current resource observations/reservations and the existing Method/Hands boundary; add a typed request or journal field only if that working path needs it, with its own boundary test and owning contract update.
- [ ] 2.3 Reassess interrupted, blocked or worsening feeding through existing reviews/events, including suitable alternate care or food and explicit blockers when none is feasible. Verify tests do not treat release or job start as success, do not misclassify normal bottle accumulation as a stall, retain actual partial intake and do not replay consumption. Document how the observed state triggers the response soon enough to avoid preventable harm.

## 3. Prove the native feeding response

- [ ] 3.1 Add a short staged bottle-feeding case: hungry baby, suitable food and an eligible adult initially held by observed competing governor work, with normal shared selection enabled. Verify bot coordination permits vanilla feeding, actual intake occurs before the staged harmful deterioration and ordinary work cannot reclaim the caregiver during safe return. The harness must neither assign feeding nor free the caregiver after setup; stop when this feeding lifecycle claim is observable. Record the case's claim and registered/unexecuted status in the food evidence page when authored.
- [ ] 3.2 Add a separate short native nursing case with observed usable milk and an eligible caregiver under normal control. Verify actual intake and the associated care lifecycle rather than a manually bound food input; record the distinct claim and actual proof limits in the food evidence page. Use about one minute expected wall time per case, under two including startup, and stop to diagnose overruns rather than extending budgets.
- [ ] 3.3 Exercise interruption/source loss, unavailable care and multiple babies competing for finite capacity in focused regressions. Verify every confirmed baby scenario has a working response or an honest unmet need when no feasible response exists. For each additional forecast or execution constraint, record the concrete failure, reproducing test, why ordinary observation/reaction is insufficient and the smallest correction; fix demonstrated in-scope gaps without adding the old exact-input machinery by default. Update the owning contract and evidence with each necessary correction.

## 4. Integrate and verify the completed behavior

- [ ] 4.1 Complete the orphan sweep across superseded assumptions, fields, native reads, requests, codecs, helpers, tests and comments; verify searches show no newly unused code and documentation links from `docs/README.md` still resolve. Update the food evidence page with the scoped baby audit, #4040 and actual implementation/evidence limits, preserving the confirmed requirements and historical provenance.
- [ ] 4.2 Run `openspec validate --all --strict --no-interactive` and `RimGovernor.cmd test > test.out 2>&1`, wait for final PASS/FAIL and record the milestone result. Execute the targeted native cases locally when establishing their claims; retain source/run provenance, actual results and anything unexecuted in the evidence page and commit body. Registration or a passing code gate alone does not prove feeding works; required native verification pending means #4040 stays open.

## Workflow follow-up

- Commit and land the completed behavior through AGENTS.md's lane, reporting checks, remaining verification, rules 7–9 and Complexity. Native acceptance is not a prelanding gate; do not report the fix complete or use a closing keyword while required outcome verification remains pending.
- Synchronize the reviewed food delta and archive this change only through the explicit sync/archive workflows after implementation is complete. Leave the separate `redefine-food-requirements` capture alone.
