# Verify native forecasts

[Documentation](../README.md) · [Forecast contracts](../reference/forecast-contracts.md)

`test_food_forecast.py`, `test_native_forecasts.py` and `test_power_forecast.py`
cover native-shaped diet/access partitions, inventory ownership, competing animal
feed, rot deadlines, unknown labor and mood thresholds, and persisted risk recovery.
The full [Docker suite](docker-checks.md) checks controller and chat integration.

Compile the task-local observation project against [Linux references](docker-inputs.md)
with `-p:ForecastFixture=true`. Stage its observation/identity DLLs in a private mod
snapshot. After setting the [Docker input/output variables](docker-worker.md), run
from the task worktree:

```powershell
docker compose -f containers/compose.yaml -p rimbot-b08 run --build --rm worker -- python /app/scripts/native_forecast_acceptance.py
```

The script uses the staged `RIMBOT_BRIDGE_ROOT`. Its result is retained at
`run/forecast-result.json` beside `inputs.json` and private logs. Each attempt needs
a fresh output directory and container. Keep failed evidence.

The fixture explicitly spawns a generator/battery/lamp, an animal, feed and a small
crop patch, and seeds rice close to its native rot boundary. It verifies native
input readability and crop work, then ordinary ticking discharges the battery and
rots the rice. A test-only refuel supplies the generator; actual native charging
must raise stored energy and clear the reserve-risk latch. An injected unavailable
read and controller serialization retain risk before recovery. Rice must reach its
native `Rotting` stage, distinguishing spoilage from consumption or lost access.
Verified advancing controller review pauses are allowed; danger stops still fail.

Fixture tools are excluded from production builds and the model gateway. This
accepts bounded forecasts and simulation progression, not ordinary construction,
feeding jobs or sustained survival. Those belong to the food/foothold acceptance
matrix. Rebuild without the fixture flag for normal use.
