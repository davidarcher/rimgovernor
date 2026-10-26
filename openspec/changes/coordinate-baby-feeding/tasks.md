# Tasks

Implement [#4040](https://github.com/davidarcher/rimgovernor/issues/4040) against
[the delta](specs/food-security/spec.md) and [design](design.md). All boxes are
implementation work, not completed work or execution evidence. Groups are ordered
dependencies within one coherent behavior milestone; do not land a partial path
as the completed fix.

## 1. Exact native feeding facts and requests

- [ ] 1.1 Inspect the installed breastfeeding and bottle-feeding drivers, their typed job/workgiver routes and reservation/input semantics; record the supported mechanics in the owning forecast/pawn-activity contracts and verify them against the game assembly before a native rebuild.
- [ ] 1.2 Extend canonical GameFacts baby-care observations with exact caregiver/recipient/source alternatives, approach and service bounds, finite milk/refill and named unavailable states; verify boundary tests cover reachable, blocked, depleted and already-active feeding without turning conditional nursery opportunities into guaranteed service.
- [ ] 1.3 Extend GameOrders' existing Work activity with a typed feeding payload and carry it through candidate binding, Method action/journal codecs and native request validation; verify round-trip/invariant tests preserve mode, recipient, source/count and ownership, and update action/persistence contracts for the existing owners without adding a store.

## 2. Shared feeding demand, supply and consequences

- [ ] 2.1 Derive per-baby assisted-feeding demand under Food and generate breastfeeding/bottle-feeding alternatives in coordinated composition; verify candidate-to-comparison tests include travel/service delay and recipient useful intake/hunger exposure without making the baby productive labor or double-crediting native nursery care.
- [ ] 2.2 Include caregiver time, exclusive baby targets, physical food lots/counts and caregiver-specific finite milk/refill in shared feasibility and Method admission; verify tests reject overlapping carers, duplicate feeding, overcommitted milk and shared-food overdraw across babies and adult eating while allowing compatible sequential feeds.
- [ ] 2.3 Replace the count-only `Breastfed` provisioning exemption with shared feasible nursing coverage and residual baby-food demand; verify FoodFamily tests keep unavailable/depleted nursing unmet, consider available bottle feeding, reduce only actually covered provision and never credit ingredients or promised output as intake. Document the provision/feeding distinction in support-work and forecast contracts.
- [ ] 2.4 Expose remaining food versus caregiver/access blockers through the existing explanation path; verify the no-care/no-source tests retain unmet feeding demand with a specific reason and no assumed-success option, and document the supported baby scope.

## 3. Competing work and exact native feeding

- [ ] 3.1 Extend incumbent comparison/release for the needed busy-caregiver path, pricing retained work and preserving partial progress, inputs, carried obligations and dependent participants; verify generation-through-binding tests choose timely feeding over justified competing work, retain a true tie and reject a forecast whose assumed release cannot execute. Update voluntary-control and continuation contracts.
- [ ] 3.2 Carry selected releases through the existing journal and Hands lifecycle, requiring fresh released ownership before replacement admission; verify runtime/admission tests cover stale revisions, losing candidates that release nothing, independently continuing participants and protected/non-cancellable work without overlapping ownership.
- [ ] 3.3 Add the exact native feeding Work adapters using verified native feeding jobs; verify start guards and focused boundary tests reject wrong recipients, unsuitable or changed sources and insufficient milk, and preserve the selected source/count instead of silently substituting another. Update pawn-activity and action-completion contracts.
- [ ] 3.4 Reconcile observed feeding progress and interruption through coordinated continuation; verify tests retain partial observed intake, rederive remaining demand after source/caregiver loss, release only this attempt's claims and never replay consumption from an uncertain receipt. Ensure an already-active native feed remains occupied care rather than a second free option.

## 4. Native outcome evidence and integration

- [ ] 4.1 Register a short staged bottle-feeding case with a hungry baby, suitable food and the eligible caregiver observably committed to competing governor work under normal shared selection; verify typed assertions establish the incumbent, governor-selected feeding identity and actual baby intake, stopping on that one claim. Add its claim and registered/unexecuted status to the food evidence page when authored.
- [ ] 4.2 Register a separate short breastfeeding case with observed usable charge and eligible pair, normal shared selection and exact feeding identity; verify typed assertions establish actual baby intake through the nursing adapter rather than an idle native action. Record its distinct claim and proof limit in the food evidence page when authored.
- [ ] 4.3 Complete the orphan sweep through every reader of the replaced count-only provision path and changed request/fact shapes; verify repository searches show no newly dead fields, native reads, codecs, helpers, tests or misleading comments, and confirm owning contract links remain reachable from `docs/README.md`.
- [ ] 4.4 Validate the completed change with `openspec validate --all --strict --no-interactive` and the milestone's `RimGovernor.cmd test > test.out 2>&1`; wait for final PASS/FAIL and record the result. Run focused native cases locally when establishing their claims, with about one minute expected per case and under two with startup; stop and diagnose overruns without increasing budgets. Record exact source/run provenance, actual results and any unexecuted verification in the food evidence page and commit body. A registered case or passing gate alone is not native feeding proof; keep #4040 open while required native outcome verification remains pending.

## Workflow follow-up

- Commit and land the completed implementation through AGENTS.md's lane, with checks, remaining verification, rules 7–9 review and the Complexity report. Native execution is not a prelanding gate; pending required outcome evidence must remain explicit.
- Close #4040 only when its entire acceptance, including required outcome verification, is met; omit a closing keyword while that verification remains pending.
- Synchronize the reviewed food delta and archive the completed change using the explicit sync/archive workflows. Do not archive the separate `redefine-food-requirements` capture as part of this fix.
