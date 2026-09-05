# Bed shortage gameplay regression

Close RimWorld and leave LM Studio serving the selected model, then run from the repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/benchmark.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/benchmark.ps1 -Reasoning medium
```

The first run generates and saves `tmp/bed-benchmark/Saves/RimBot-bed-shortage-v1.rws`. Every measured run reloads that same save. Keep it to compare revisions/settings. The fixture contains three colonists, one completed wooden bed, two bed blueprints, 36 allowed wood and 100 forbidden wood. Fixture setup deliberately uses test-only world creation; the measured manager uses the production tool execution path and normal pawn construction. It does not complete frames or grant resources during evaluation.

Pass: three built beds and no colonist lost. Fail: ten measured minutes or 100 tool calls without completion. Reports include model/settings, time, game ticks, tool calls, repeated results, recovery requests and tokens. The game exits automatically. A separate 14-minute process timeout covers loading failures.

Results: `tmp/bed-benchmark/benchmark-result.json`; previous results are timestamped. Inspect `Player.log` in the same directory for exact calls. The runner uses an isolated save/config directory and temporarily swaps only the mod DLL, restoring and hash-checking the installed DLL in `finally`. Do not launch another RimWorld instance during the test. If the runner itself is forcibly terminated, restore `tmp/bed-benchmark/installed-RimBot.dll.backup` to the installed mod before launching normally.

This tests recovery from an existing construction shortage, not autonomous base design, seasonal planning, combat or safety across arbitrary maps. The fixture is repeatable; model sampling and real-time simulation can still vary. Run multiple trials before claiming reliability. No benchmark code ships in the normal package.

## Verified runs, 2026-09-05

Qwen3.5-9B, execution reasoning `none`, same saved fixture:

| Run | Result | Measured seconds | Tool calls | Recovery requests |
| --- | --- | ---: | ---: | ---: |
| First uninterrupted evaluation | Passed | 19.15 | 11 | 0 |
| Background loading and evaluation | Passed | 28.32 | 10 | 0 |

The runner now sets `runInBackground` in its isolated Prefs.xml before startup. Unity's flag alone was overridden during loading. Earlier startup/manual-interference attempts produced no evaluation result and are not counted as passes.

Both evaluations allowed the missing wood and normal pawns finished the beds. Stale blueprint IDs and already-allowed supply calls remain observable inefficiencies. Neither pass exercised the reasoned repetition-recovery branch; its repetition detection and persistence have separate regression checks. These two passes are not a reliability estimate for arbitrary colonies.
