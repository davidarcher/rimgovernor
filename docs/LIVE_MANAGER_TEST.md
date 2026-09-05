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
