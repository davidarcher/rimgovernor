# Local simulation speed

The background interactive game still renders. Disabling dashboard capture is not
equivalent to disabling Unity rendering. A separate headless mode is now available.

## Headless launch

Close the controller and RimWorld, then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build_headless.ps1 -Install
powershell -ExecutionPolicy Bypass -File launch.ps1 -Headless -NoBrowser
```

The dashboard remains available at port 8787, with native state/chat/controls and
an explicit headless notice instead of screenshots. No visual adviser images are
available. Restart without `-Headless` to watch an interactive colony.

The launcher derives a separate `.rimbot/bridge/headless-profile` and GABS config
from the test baseline. It does not change the normal or interactive test profile.
It enables the source-built HeadlessRimPatch adapter and adds `-batchmode -nographics`.
It still waits for native fade readiness: `playable` alone is too early for player
clock control, even without a display. No GPU screenshots or OS input are used.

Source and local compatibility changes are recorded in
`integrations/headless-rim/PROVENANCE.md`. The upstream Linux Docker launcher is
research only; no RIMAPI or Linux installation is required for this Windows path.

For an isolated benchmark (controller/game closed):

```powershell
.venv\Scripts\python.exe scripts/native_speed_benchmark.py --headless --seconds 3 --repeats 2
.venv\Scripts\python.exe scripts/native_strategy_smoke.py --headless
```

Validated: launch, repeated baseline reloads, native observations, clock advancement,
instant zone creation/readback, duplicate avoidance and cleanup. This does not yet
prove long colony survival, visual parity or every native action under headless mode.

`scripts/native_speed_benchmark.py` reloads the same eight-tribal baseline before
each sample, uses RimBridgeServer's native `play_for` forced-speed support, and
restores Paused with boost disabled. Run only with the controller/game closed.
It is a disposable-test utility, not a normal strategist capability.

Initial local measurements (two samples per speed, three seconds requested):

| Speed | End-to-end ticks/sec | Completed sample? |
| --- | ---: | --- |
| Normal | 58, 58 | Yes |
| Superfast | 349, 349 | Yes |
| Boosted Ultrafast | 2,406, 2,042 | No: pause within 0.32 seconds |

Headless comparison, same fixture and requested durations:

| Speed | End-to-end ticks/sec | Completed sample? |
| --- | ---: | --- |
| Normal | 60, 60 | Yes |
| Superfast | 357, 357 | Yes |
| Boosted Ultrafast | 3,321, 3,643 | No: pause within 0.31 seconds |

Normal/Superfast remain speed-limited, so removing rendering offers little increase
there. The higher boosted numbers are transient and not a sustained speedup claim.
RocketMan was excluded: the installed Workshop metadata only lists through 1.5,
while this test is RimWorld 1.6; compatibility needs a separate check.

The bridge reported an external/game pause during both boosted samples. Its cause
has not been identified. These are transient rates, not evidence for sustained
2,000+ TPS or complete training-episode throughput. The script also records the
native tick-window rate separately from end-to-end request timing.

Next measurement: identify the pause source, collect sustained samples across
colony ages, then compare rendering/capture modes. Include model wait time and
observation/action overhead when measuring full episodes. Faster simulation does
not accelerate inference and may simply make the decision loop the bottleneck.

Native capability reference:
https://github.com/pardeike/RimBridgeServer/blob/main/docs/tool-reference.md
