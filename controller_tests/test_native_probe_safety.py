import importlib.util
import asyncio
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts'))
from native_scenario_support import settle_dispatch


spec = importlib.util.spec_from_file_location('native_combat_probe', Path(__file__).resolve().parents[1]/'scripts/native_combat_smoke.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def test_retreat_recognizes_recorded_native_letter_pause_once():
    # Native pawn-matrix retreat evidence: the warning was attributed to vanilla,
    # rather than external player input. The patient remained safely paused.
    clock = dict(stopReason='letter_pause', pauseVerified=True, active=False,
        stopDetail='Game was paused by VANILLA, not by a person: Ancient danger (ThreatBig, pauseMode MajorThreat) arrived')
    assert probe.ancient_warning_pending(clock, {})
    assert not probe.ancient_warning_pending(clock, {'acknowledged_ancient_warning':clock['stopDetail']})
    assert not probe.ancient_warning_pending(dict(clock, stopDetail='Raid'), {})
    assert not probe.ancient_warning_pending(dict(clock, stopReason='injury'), {})


@pytest.mark.asyncio
async def test_in_flight_probe_dispatch_is_observed_without_another_order():
    progress = SimpleNamespace(state='executing')
    rt = SimpleNamespace(connected=True, current_plan=SimpleNamespace(progress={'wood':progress}),
                         execute_manual_requests=AsyncMock())
    async def receipt():
        await asyncio.sleep(0)
        progress.state='complete'
    task = asyncio.create_task(receipt())
    await settle_dispatch(rt, [SimpleNamespace(id='wood')])
    await task
    assert progress.state=='complete'
    rt.execute_manual_requests.assert_not_awaited()
