from types import SimpleNamespace

import pytest

from rimbot.strategic_state import StrategicState, features


def batch(buildings):
    return SimpleNamespace(native={'buildings': buildings}, summary=SimpleNamespace(
        end_tick=100, pawns=[], supplies=[], visible_rooms=0, zone_count=0,
        fogged_rooms_omitted=0, hostile_count=0, hunting_predator_count=0,
        alert_labels=[], warnings=[]))


def power_events(state):
    return [event['kind'] for event in state.pending if event['kind'].startswith('power.')]


@pytest.mark.parametrize('unavailable', [
    {}, {'powerNets': None},
    {'powerNets': [], 'powerSummary': {'readable': False, 'error': 'Unavailable manager'}},
    {'powerNets': [{'netW': None, 'storedWd': 20}]},
    {'powerNets': [{'storedWd': 20}]},
    {'powerNets': [{'netW': -100, 'storedWd': None}]},
    {'powerNets': [{'netW': 100}, {'netW': -100}]},
])
def test_unknown_power_does_not_claim_reserve_recovery(unavailable):
    state = StrategicState()
    state.update(batch({'powerNets': [{'netW': -100, 'storedWd': 10}]}))
    assert power_events(state) == ['power.reserve_low']
    state.decided()
    for _ in range(3):
        state.update(batch(unavailable))
        assert state.latches['low_power'] is True
        assert power_events(state) == []
        state = StrategicState(state.dump())
    state.update(batch({'powerNets': [{'netW': -100, 'storedWd': 100}]}))
    assert power_events(state) == ['power.reserve_recovered']
    assert state.latches['low_power'] is False


def test_unreadable_native_empty_grid_is_unknown():
    assert features(batch({'powerNets': [], 'powerSummary': {'readable': False}}))['power'] is None
    assert features(batch({'powerNets': [], 'powerSummary': {'readable': True}}))['power'] == []


def test_one_low_network_proves_risk_despite_another_unknown_network():
    state = StrategicState()
    state.update(batch({'powerNets': [{'netW': None}, {'netW': -100, 'storedWd': 10}]}))
    assert power_events(state) == ['power.reserve_low']
    assert state.current['power'][0]['net_w'] is None
    assert state.current['power'][0]['days_at_current_deficit'] is None
    assert state.current['power'][1]['days_at_current_deficit'] == .1


@pytest.mark.parametrize('nets', [[], [{'netW': 0}], [{'netW': 100, 'storedWd': None}]])
def test_known_non_depleting_grid_can_clear_reserve_risk(nets):
    state = StrategicState({'latches': {'low_power': True}})
    state.update(batch({'powerNets': nets, 'powerSummary': {'readable': True}}))
    assert power_events(state) == ['power.reserve_recovered']


def test_unknown_power_does_not_create_safe_latch_or_nutrition_forecast():
    state = StrategicState()
    state.update(batch({}))
    assert 'low_power' not in state.latches
    assert state.current['resources']['food_days'] is None
    assert state.current['resources']['expected_harvest'] is None
