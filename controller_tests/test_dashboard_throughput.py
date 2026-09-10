import importlib.util
from pathlib import Path

spec=importlib.util.spec_from_file_location('dashboard_throughput',Path(__file__).resolve().parents[1]/'scripts/dashboard_throughput.py')
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def sample(at,tick,session='a',connected=True,paused=False):
    return dict(at=at,tick=tick,session=session,connected=connected,paused=paused)


def test_throughput_keeps_pauses_in_wall_denominator():
    result=module.summarize([sample(0,0),sample(2,600,paused=True),sample(4,600,paused=True)])
    assert result['wall_tps']==150
    assert result['both_endpoints_paused_seconds']==2


def test_load_rewind_and_disconnect_do_not_fabricate_progress():
    result=module.summarize([sample(0,600),sample(2,0),sample(4,100,session='b'),sample(6,200,session='b',connected=False)])
    assert result['wall_tps'] is None
    assert result['excluded_intervals']==3


def test_order_rate_excludes_counter_resets():
    rows=[sample(0,0),sample(2,100),sample(4,200)]
    for row, value in zip(rows,[10,16,2]):
        row['counters']={'actions':value}
    result=module.summarize(rows)
    assert result['counter_rates_per_second']['actions']==3
    assert result['counter_rates_per_second']['tools'] is None


def test_observation_age_uses_only_valid_connected_intervals_and_ordered_ticks():
    rows = [dict(sample(0, 100), observation_tick=90),
            dict(sample(1, 400), observation_tick=100),
            dict(sample(2, 500), observation_tick=600),
            dict(sample(3, 10), observation_tick=0),
            dict(sample(4, 1000, connected=False), observation_tick=0)]
    result = module.summarize(rows)
    assert result['observation_age_ticks'] == {'samples': 1, 'maximum': 300, 'mean': 300}
    assert result['excluded_intervals'] == 2
