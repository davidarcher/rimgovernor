from copy import deepcopy
from types import SimpleNamespace

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
