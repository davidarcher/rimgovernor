# Schema-bound native commitments

Implemented 2026-09-08. `commit_plan` and `commit_steps` no longer advertise an
open argument dictionary for native operations. `describe` adds a branch with an
exact tool name and its discovered native input schema. Semantic construction and
zone actions remain available before discovery. Contracts needed by existing
native plan steps are loaded before a review so preserving a plan still works.

The binding copies the native schema; it does not mutate inspection contracts.
Each command validates against its own fields. The existing no-watch/no-cheat
gateway restrictions are reflected in the advertised execution schema, and
instant gear dropping remains unavailable. Runtime native validation and preview
checks still run before writing. Schema-valid arguments do not prove that an
action is appropriate, eligible, or complete.

## Live regression

`scripts/execution_schema_smoke.py` launches an isolated headless eight-tribal
fixture and asks the real local model to enable one pawn's self-tending. It feeds
the discovered contract, validates the returned commitment, executes through
the normal plan/Hands path, and verifies native readback with the game paused.
This is a narrow instructed execution test, not autonomous colony strategy.

The test uncovered a companion mismatch: ListPawns returns `GetUniqueLoadID()`
at the top level, but PawnConfig only resolved `ThingID`. PawnConfig now accepts
both exact native identities before considering names; no Python ID rewriting
was added. The updated native DLL was built and installed with the game closed.

Final run: Qwen 3.5 9B, one model request, 3,639 prompt tokens and 427 completion
tokens (279 reported reasoning tokens). Setting readback and completed plan-step
state passed while paused. Earlier attempts exposed test handling of optional
presentation fields, the GABS runtime-file rename failure, and the ID mismatch;
they were not gameplay successes. Evidence is under the generated
`.rimbot/execution-schema-*/execution-schema-smoke.json` directories.

190 Python tests passed. Native build succeeded with zero errors; NuGet advisory
lookup produced two NU1900 warnings because its network endpoint was unreachable.

Still needed: targeted real-model supply access, work-priority and bill tests;
general modal choices/closing with AI-owned pause recovery; broader startup
acceptance. The 20-run campaign's failure result remains unchanged.
