# Run local checks

[Documentation](../README.md)

Run the controller and dashboard checks for a prepared local development environment.

Run commands from the repository root. Keep generated evidence outside commits and
preserve failed results.

```powershell
.venv\Scripts\python.exe scripts\generate_bridge_observation.py --check
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` runs Python tests, dashboard typechecking, Vitest and the Vite build. It
does not compile/install native DLLs or run model/gameplay tests. For a focused Python
check use `.venv\Scripts\python.exe -m pytest -q controller_tests/test_NAME.py`.
Generated dashboard assets, local databases and logs remain outside commits.

With RimWorld closed, build/install native changes using:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_observation_bridge.ps1 -Install
```

Never replace installed DLLs while any RimWorld instance is running. Native build
success is not gameplay acceptance. Preserve source provenance beside copied code.

## Spatial and scheduling coverage

Shared spatial controller checks are in `test_spatial_constraints.py`,
`test_plan_geometry.py` and `test_construction_preflight.py`. They cover entrances in
all rotations, indoor farm refusal, retained-building and same-batch native footprint
conflicts, unknown geometry, and refusal before dispatch. These use native-shaped
fixtures; B06 still requires ordinary pawn construction, live player edits and observed
access acceptance.

`test_shell_site.py` exercises complete native zone census validation, enclosed farm
refusal, allowed indoor stockpiles, immediate doorway access in all rotations,
unknown/truncated geometry and direction changes. A serialized partial-dispatch fixture
adds a farm between batches and verifies no further shell orders while retaining the
first receipt. Real zone edits and pawn route behavior remain B06 gameplay acceptance;
fixtures are not game observations.

`test_shell_connectivity.py` checks four-neighbor interior and local exterior
connectivity, projected neighboring shells, clipped map edges, large-room query caps,
unknown cells and interrupted batch persistence. The bounded exterior margin does not
certify a route to a pawn or unrestricted map-wide connectivity.

Run `scripts/spatial_site_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` with `controller` on `PYTHONPATH` for a paused isolated native
spatial probe. It checks shell preflight, an allowed interior stockpile, and shared
admission refusal after a real interior farm is designated. Complete censuses, unchanged
plan/orders/native tick, zero inference and per-case timings are retained with the input
manifest. This is admission acceptance, not ordinary construction or route traversal.
Keep the installed DLL set fixed throughout the owned game; the probe never replaces
DLLs and stops its isolated session on completion/failure.

`test_project_resource_scheduling.py` covers resource competition in ready order,
dependency gates, uncertain writes, persisted receipts and Hands restock recovery.
Admission still requires enough stock for all accepted commitments. B07 native
acceptance must observe real production consumption and construction progress; the
fixture suite does not establish pawn work or save-rewind recovery.
`test_goal_watchdog_recovery.py` replays delayed tracked completion after a durable
timeout hold, including shell-to-furnishing continuation, dependent chains and refusal
under cancellation, Manual, rewind, failure or a different blocker. Native delayed labor
and paired restart acceptance remain separate B07 checks.

`test_zone_project_postconditions.py` and `test_building_facing_postconditions.py` cover
persisted exact zone and orientation expectations, native-shaped edit invalidation of
dependent work, incomplete-read recovery and legacy records. These controller fixtures
do not establish actual in-game zone or rotation editing.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
