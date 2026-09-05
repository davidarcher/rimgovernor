# Live manager execution milestone

Validated on September 5, 2026 with `qwen/qwen3.5-9b`: all five manager
proposals, preliminary/final administrator coordination, one native work toggle,
independent uncached readback, tracker status `complete`, and verified restoration.
The passing run took 80.84 seconds and 16 model calls (96,663 aggregate input
tokens, including repeated context; 6,297 output tokens). This is a correctness
milestone, not an efficiency result.

Run from the repository with RimWorld and LM Studio running, a disposable colony
loaded, the game unpaused, and the dashboard set to Manual:

```powershell
.\.venv\Scripts\python.exe scripts/live_manager_smoke.py --execute
```

This uses the configured local model, all five managers, the production
`Runtime.coordinate` administrator/labor arbitration path, and `Runtime.execute`.
It supplies a narrow player objective and relevant observed native contracts.
It does not inject a prewritten proposal, approval, or completion check.

The objective toggles one enabled pawn work assignment off. The test requires:

- All five managers return valid proposals.
- The administrator approves exactly one Workforce action matching the objective.
- The model's completion check is false before sending the order.
- RIMAPI accepts one native order.
- An uncached single-pawn read confirms the intended setting changed.
- The production work tracker records completion.
- Cleanup restores and reads back the original setting.

Evidence is saved in `.rimbot/live-tests/<timestamp>/report.json` and `trace.sqlite`,
including failed attempts. No generated evidence is committed. A timeout or failed
restoration fails the test. Use `--timeout 240` to change the model-cycle timeout.

The test temporarily changes the loaded colony. Do not enable dashboard automation
or change colonies during it. It does not restart RimWorld or install mod DLLs.

## What this does and does not establish

This establishes an actual model-to-manager-to-administrator-to-game execution
path for a bounded work-toggle objective. It does not establish autonomous colony
planning, construction geometry, combat competence, or good inference performance.
Seasonal planning is outside this focused test. The next scenarios should exercise
resource access and blueprint placement with independent native readback.

## Native response details discovered by the test

RIMAPI's bulk v1 colonist details cache lasts 1,800 game ticks. Single-pawn details
are uncached. Its work-priority list includes only enabled work and is sorted by
priority; disabled work disappears instead of returning priority zero. Therefore,
checks must identify a work type rather than rely on a stable array index.

## Construction scenario

Passed on September 5, 2026 with `qwen/qwen3.5-9b`: observed
`Blueprint_Bed` (wooden bed, rotation 0, 1x2 footprint) at map 0 cell (143,147),
native thing ID 38522, and placement tracker status `complete`. The run took
82.78 seconds, 14 model calls, 58,796 aggregate input tokens and 6,505 output tokens.

```powershell
.\.venv\Scripts\python.exe scripts/live_construction_smoke.py --execute
```

This scenario requests exactly one north-facing wooden bed blueprint. It resolves
the requested bed and wood labels from the running game's definitions, finds a
nearby empty footprint, checks it with RIMAPI, and supplies those observations to
the same production manager/administrator coordinator. The model must assemble
the building payload and completion check. The test rejects additional orders,
different definitions/materials, different positions, floor placement, or clearing
obstacles before calling the production executor.

An independent exact-cell query must observe one bed blueprint and the production
tracker must mark the placement complete. The blueprint stays in the disposable
test game for inspection. Evidence is saved under `.rimbot/construction-tests`.
This verifies blueprint placement, not material delivery, completed furniture,
or autonomous shelter layout. The scenario's fixed furniture objective and
north-facing footprint are test inputs, not a production room planner.

RIMAPI's `get_map_things` only returns haulable items. The radius query includes
items/buildings/plants and excludes blueprints. Exact-cell queries include
blueprints and frames, making them suitable for immediate placement readback.

## Bed completion (September 5 follow-up)

The actual game reached `Blueprint_Bed` ID 17925 → `Frame_Bed` ID 18042 →
finished `Bed` ID 18046 at (129,139), with good quality. The managers identified
forbidden nearby timber and issued one native allow order. Colonists delivered
materials and constructed the bed through normal work. No additional bed was
placed. Evidence: `.rimbot/bed-completion-tests/20260905-163313/report.json`.

```powershell
.\.venv\Scripts\python.exe scripts/live_bed_completion.py --execute --blueprint-report .rimbot/construction-tests/20260905-163126/report.json
```

Use a successful blueprint report from the currently loaded disposable game.
The fixture allows normal work orders and selected nearby timber, observes the
finished building independently, and pauses the game afterward. This is still a
bounded completion test, not a claim of autonomous shelter design.

Completion took 86.47 seconds, 29 model calls, 287,545 aggregate input tokens and
5,714 output tokens. Most elapsed time preceded material delivery; after the frame
was observed, the finished bed appeared three seconds later at test speed 3.
The aggregate token count includes repeated context across calls, not a single
context window. The test exposed repeated misnested query filters and attempts
to write through the read-only query tool. Unambiguous misplaced read filters are
now normalized; native arguments and conflicting filters are never guessed.
These follow-up changes have regression coverage but were added after this run.

Fresh quicktest sessions were also verified to produce different controller
identities and empty plans/chat/work despite identical seed/map/faction IDs.
RIMAPI now exposes a loaded-game session identifier and scopes native response
caches to it. Loading a saved game starts a new session; controller reconnection
to the same running game preserves its state.
