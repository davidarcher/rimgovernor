# Local simulation speed

The background game still renders. Disabling dashboard capture is not equivalent
to disabling Unity rendering. Headless RimWorld has not been validated here.

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
