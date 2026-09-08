# Eight-tribal headless campaign — 2026-09-08

**Result: 0 of 20 runs reached the combined starter foothold.** Five runs created
a verified nearby stockpile. Run 13 also completed a 6×5 wall-and-door shell.
No run established eight sleeping places or allowed the starting pemmican. All
final observations still contained eight living colonists; these short runs do
not establish survival. Model prose was never treated as completed construction.

The campaign recorded 492 completed model calls and 100 native action receipts.
Receipts include blueprints and notification actions, not just useful finished
work. Runs 8–9 placed doors around their perimeters and are failures, not rooms.

## Method and limits

- Same saved eight-tribal fixture, fresh controller/plan/memory state each run.
- Local `qwen3.5-9b`, reasoning requested, 65,536 configured context, 8,192 maximum
  output tokens, temperature 0.35. No paid API requests.
- Headless Windows, ordinary Superfast after review. 120-second initial window,
  extended to 120 seconds after first action; earlier stop on a control hold.
- Success required eight nearby completed beds/spots, a nearby stockpile of at
  least nine cells, allowed starting pemmican, and eight living colonists.
- One known sealed Ancient Danger proximity warning could be acknowledged by the
  fixture after checking no observed hostiles/injuries. Other holds stopped it.
  This exception is test-only; production pause policy was not relaxed.
- Adaptive debugging: code changed between runs. These are not independent
  trials of one fixed build or a controlled model comparison. Revision is HEAD at
  worker launch; edits during a campaign can precede their checkpoint commit.
- A separate two-instance native lifecycle test ran briefly alongside the
  campaign. It did not use the LLM. No parallel-model throughput claim is made.

Raw evidence: `.rimbot/headless-campaign-20260908/summary.json`, and each
`iteration-NN/result.json` / `state.sqlite`. Logs remain local, outside Git.

| Run | Revision at launch | Model calls | Action receipts | Stockpile | Seconds |
|---|---|---:|---:|---|---:|
| 1 | b51b7c2 | 45 | 0 | No | 120.4 |
| 2 | b51b7c2 | 40 | 0 | No | 120.3 |
| 3 | 06341ac | 11 | 0 | No | 56.1 |
| 4 | 8682435 | 44 | 0 | No | 120.3 |
| 5 | 3da10c2 | 30 | 0 | No | 120.3 |
| 6 | 37a7530 | 43 | 0 | No | 120.3 |
| 7 | 46dbf30 | 28 | 0 | No | 120.4 |
| 8 | b23a49d | 22 | 24 | No | 188.4 |
| 9 | b23a49d | 17 | 25 | No | 156.5 |
| 10 | 3273cd2 | 16 | 0 | No | 120.4 |
| 11 | 0106174 | 19 | 0 | No | 120.6 |
| 12 | a0fbc5d | 19 | 0 | No | 120.4 |
| 13 | a0fbc5d | 25 | 21 | Yes | 172.6 |
| 14 | 5d1eb75 | 29 | 13 | Yes | 200.5 |
| 15 | 7b89606 | 16 | 2 | Yes | 92.4 |
| 16 | 7b89606 | 25 | 13 | Yes | 172.4 |
| 17 | f7cb11b | 13 | 0 | No | 120.4 |
| 18 | 2bfe06e | 18 | 0 | No | 120.5 |
| 19 | 2bfe06e | 16 | 0 | No | 120.3 |
| 20 | 2bfe06e | 16 | 2 | Yes | 120.4 |

## Changes made and what remains

- Added incremental `commit_steps`, preserving existing work and goals. The
  model still often bundled unrelated tasks into a rejected large plan.
- Removed a material-availability gate that prevented legal native blueprints.
  Actual completion remains tied to observed buildings, not placement receipts.
- Calibrated the conservative context estimate against measured prompt usage,
  with a bounded factor and retained output reserve. This reduced premature
  compaction; it did not solve planning.
- Grounded construction names in discovered catalog definitions. The first
  version exposed only a partial page's vocabulary; corrected it with complete
  category indexes for buildings and Orders. This did not guarantee appropriate
  choices or placement. Same-definition wall/door shells are now rejected.
- Surfaced existing startup guidance and the native Architect Orders route for
  allowing supplies. The model still did not execute food access successfully.
- Added periodic review of unresolved hunger/material shortages after unrelated
  decisions cleared their original events.
- A temporary GABS runtime-file rename/read failure incorrectly invalidated a
  project permanently. Such observation failures now wait for fresh evidence,
  retain issued receipts and recover without replay. Root GABS failure remains
  to investigate; the recovery is covered by a regression test.
- Opening notification dialogs can still stop autonomous play. Full letter text
  is available without opening them; guidance now makes that distinction clear.
  Native modal choice/close control and AI-owned pause recovery are still needed.

The next priority is **schema-bound native execution arguments**, followed by
small real-model acceptance cases for supply access and placement. Runtime
validation catches invented fields, but the commitment schema still permits an
arbitrary native argument dictionary. More unrelated game capabilities alone
will not repair that interface. Then repeat the combined foothold test before
claiming a playable colony manager. See the [current queue](RIMBRIDGE_MIGRATION.md).

Parallel headless lifecycle isolation passed separately; commands and Linux/
container work are documented in [headless campaigns](HEADLESS_CAMPAIGNS.md).

## Final regression checkpoint

187 controller tests passed. The real native scripted check
`scripts/native_strategy_smoke.py --headless --room` also passed: zone and instant
sleeping spots, legal blueprint before material access, completed pawn-built
wall, obstructed-room refusal before placement, and a full 15-wall/one-door shell
built in 19.5 seconds after fast simulation began. Replaying execution issued no
duplicates. This is scripted native acceptance, not another real-model success.
All disposable test game processes were closed after validation.
