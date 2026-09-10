from copy import deepcopy
from types import SimpleNamespace
from pathlib import Path
import asyncio
import pytest

from scripts.startup_milestones import StartupMilestones


def runtime():
    return SimpleNamespace(context_token='load', clock={'ticksGame': 100},
        execution_window_end=None, current_plan=SimpleNamespace(control={}),
        batch=SimpleNamespace(summary=SimpleNamespace(end_tick=100), native={
            'pawns': {'pawns': [{'thingId': 'pawn', 'job': 'Sow', 'position': {'x': 1, 'z': 1}}]},
            'buildings': {'buildings': [{'thingId': 'old', 'status': 'built'}]}}))


def test_milestones_require_native_tick_and_work_changes():
    rt = runtime()
    measurement = StartupMilestones(rt)
    rt.execution_window_end = 700
    measurement.sample(rt)
    assert set(measurement.rows) == {'first_execution_window'}
    rt.clock['ticksGame'] = 101
    measurement.sample(rt)
    assert 'first_observed_tick' in measurement.rows
    assert 'first_observed_pawn_work' not in measurement.rows
    rt.batch = deepcopy(rt.batch)
    rt.batch.summary.end_tick = 110
    rt.batch.native['pawns']['pawns'][0]['position']['x'] = 2
    rt.batch.native['buildings']['buildings'].append({'thingId': 'new', 'status': 'blueprint'})
    measurement.sample(rt)
    assert measurement.rows['first_observed_pawn_work']['pawn'] == 'pawn'
    assert 'first_observed_new_building' not in measurement.rows
    rt.batch.native['buildings']['buildings'][-1]['status'] = 'built'
    measurement.sample(rt)
    assert measurement.rows['first_observed_new_building']['thing_id'] == 'new'


def test_load_change_invalidates_timing():
    rt = runtime()
    measurement = StartupMilestones(rt)
    rt.context_token = 'other'
    rt.clock['ticksGame'] = 1000
    measurement.sample(rt)
    assert not measurement.report()['valid']
    assert not measurement.rows


def test_unchanged_or_different_job_does_not_prove_work():
    rt = runtime()
    measurement = StartupMilestones(rt)
    rt.batch.summary.end_tick = 120
    measurement.sample(rt)
    rt.batch.summary.end_tick = 140
    rt.batch.native['pawns']['pawns'][0].update(job='Wander', position={'x': 3, 'z': 1})
    measurement.sample(rt)
    assert 'first_observed_pawn_work' not in measurement.rows


@pytest.mark.asyncio
@pytest.mark.parametrize('fails', [False, True])
async def test_pause_probe_reserves_work_without_stopping_scheduler(monkeypatch, fails):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[1]/'scripts'))
    from scripts import throughput_runtime
    rt = SimpleNamespace(review_task=None, execution_task=None,
                         scheduler_task=object(), work_changed=asyncio.Event())
    scheduler = rt.scheduler_task
    async def probe(runtime, report):
        assert runtime.execution_task is asyncio.current_task()
        assert runtime.scheduler_task is scheduler
        if fails:
            raise ValueError('Native prerequisite failed')
    monkeypatch.setattr(throughput_runtime, '_verify_pause_race', probe)
    if fails:
        with pytest.raises(ValueError, match='Native prerequisite failed'):
            await throughput_runtime.verify_pause_race(rt, {})
    else:
        await throughput_runtime.verify_pause_race(rt, {})
    assert rt.execution_task is None and rt.work_changed.is_set()
    assert rt.scheduler_task is scheduler
