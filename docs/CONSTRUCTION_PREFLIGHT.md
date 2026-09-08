# Construction preflight

New building batches and room shells are checked through native
`home/place_building(dryRun=true)` before a strategic plan is committed. A shell
checks its door and complete perimeter. No blueprint is placed, and a rejected
candidate does not change the plan revision, progress, or history.

Failures return the step ID, definition, coordinate, and native placement evidence
to the strategist in the same review. Unknown definitions are not translated or
guessed. Material alternatives are tried in the requested order. Stuff-based
construction requires an explicit material choice. Stock quantities alone do not
reject a plan: obtaining materials may be other planned work.

Unchanged step intentions are reconciled by the existing executor. A new step
with dependencies still checks definition resolution but defers site readiness
until execution, since a predecessor may clear its footprint. The executor keeps
its own immediate placement checks; preflight is not an atomic reservation of
terrain, resources, or pawn eligibility.

Native-operation schema validation and shared geometry checks run before this
preflight. Colony and direction are checked again after asynchronous validation.
Zones continue through their existing native executor validation; this change is
specifically for construction. Blueprint completion still requires observation.

Tests cover an invented door repaired within the same model review, whole-shell
validation, a rejected final wall without any writes, alternate materials,
dependent clearance, and preservation of unchanged work. These are protocol and
native-call simulations, not proof of autonomous colony-building quality.

The 90-second real-model probe on 2026-09-08 made no plan commits, issued no
orders, and had zero rejected calls. It remained in inspection and therefore did
not exercise preflight. Local evidence: `.rimbot/construction-preflight-live.json`.
