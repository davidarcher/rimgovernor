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

## Stale targets and unnecessary calls iteration

The benchmark now uses the production review scheduler. It suppresses only seasonal/daily planning, which is outside this construction fixture. Earlier runs manually requested reviews on every construction-progress change, so their call counts are not directly comparable. The test-only loading driver clears native loading text to avoid waiting for an OnGUI repaint in a hidden window; no gameplay rule is changed.

Pass now also checks that the two original blueprint sites contain actual built beds and that inspecting each retired blueprint ID returns `target_gone` with current objects at its observed site. It never silently redirects an order to a replacement object.

Intermediate runs with production scheduling completed in 21.94 seconds / 2 calls and 20.44 seconds / 6 calls. The second still repeated Allow and misidentified material delivery as missing staffing. Adding native worker activity feedback completed another run in 35.95 seconds / 7 calls, still with one redundant Allow.

Final revision adds one reasoning-enabled diagnosis at review start when measured construction supplies are insufficient. Follow-up execution retains the configured reasoning setting. With Qwen3.5-9B configured `none`, the final run completed in **40.77 seconds, 3 tool calls, 42,691 total provider tokens**: Allow the two forbidden wood stacks, inspect construction progress, save plan. No stale-ID call, redundant Allow, or false blocker occurred in this run. The JSON `reasoning` field reports the configured execution setting; shortfall diagnosis overrides it to `medium` and is explicitly logged. This is one final-revision trial, not proof of general reliability or a speed improvement.

The source regression suite has 199 checks, including forgetting expired construction/action IDs without deleting colliding zone IDs. Production packaging excludes the benchmark classes.
