# Verify visual review evidence

[Documentation](../README.md) · [Native Docker inputs](docker-inputs.md)

Use the normal staged Linux game/mod/profile/GABS inputs with a fresh output:

```powershell
python scripts/container_visual_acceptance.py --game <linux-game> --mods <private-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimgovernor-worker:my-visual-task --output .rimgovernor/visual-01 --model qwen3.5-4b
```

The wrapper builds and pins the task image, starts one private rendered worker,
runs `scripts/native_visual_acceptance.py`, and removes only its own container.
It sets Xvfb, the recorded zero GC time slice, and the explicitly allowed local
LM Studio Docker-host endpoint on port 1234. `--model` must name an available
local vision model; there is no paid fallback. `--no-build` reuses an unchanged
image. The default run timeout is 1,800 seconds, separate from the build timeout.

The probe reloads the unchanged baseline for doorless and door-bearing ordinary
blueprint shells on a fully observed walkable site, checks every native blueprint definition and position, and
compares wide-only and paired-detail reports. It measures report-supported door
decisions and decisions before/after exact native evidence recall. It retains
both source PNGs, complete model reports, native truth, camera before/after,
paused ticks, model usage and cleanup under `/worker/evaluation`. A native player
pan during inference must remain in place while the report cites its original
source. Worker input
hashes and display logs remain under `/worker/run`. Process success establishes
the experiment completed, not model accuracy: inspect the screenshots and every
concern rectangle, and report the measured scores including regressions or no
improvement. Two layouts do not establish general visual reliability or actual
pawn construction/access. Reviewer output must still be verified natively.

Focused checks are `test_visual_review.py`, `test_visual_source.py` and
`test_review_evidence.py`, plus dashboard `VisualReviews.test.tsx`. They cover
stale report refusal, source integrity, camera preservation, normalized crops,
unavailable-image behavior and exact historical recall. Docker controller checks
also run these tests and the dashboard checks.
