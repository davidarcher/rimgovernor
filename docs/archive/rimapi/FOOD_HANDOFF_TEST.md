# Food handoff regression

Observed failure: Survival submitted narrative intentions with no retained orders. The administrator repeatedly inspected unrelated systems and inferred that unforbidding was unavailable. A bounded reproduction then showed rejected completion-query payloads followed by an inaccurate claim that an order was drafted.

Changes:
- Arbitration consumes supplied proposals and observations, with only its decision submission tool. It does not repeat specialist API discovery.
- The native forbidden flag command describes allow/unforbid terminology and immediate flag semantics.
- Exact-cell inspection requires its native position argument. Write-through-query errors point to native draft tools rather than the obsolete submit-actions flow.
- Rejected drafts report that nothing was retained and include the completion query contract. Submitting afterward requires either correction or explicit blockers.

Repeatable bounded test: `.venv\Scripts\python.exe scripts/live_allow_food.py --execute` with a loaded disposable colony and idle Manual dashboard. It supplies observed meal stacks, lets the real model propose and arbitrate, restricts test writes to those meal flags, and independently reads every selected stack back. It briefly runs the game for execution and pauses afterward. It leaves those meals allowed.

Passed evidence: `.rimbot/allow-tests/20260905-174313/report.json`: five stacks, one native action, six model calls, 20.5 seconds. Earlier attempts are retained: rejected drafts produced no approved action; the next approved action was deferred because the game was paused. The final test explicitly handles execution speed.

43 backend tests passed, including recovery from rejected drafts and decision-only arbitration. Broad autonomous startup remains unverified and previously failed; this test isolates one handoff, not full gameplay competence.
