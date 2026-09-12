from copy import deepcopy
import asyncio
from pathlib import Path
import sys
import sqlite3

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_mood_evidence import audit_mood_review, mood_reference, audit_mood_hold
finally:
    sys.path.pop(0)


def mood_fixture(scenario):
    person = {'thingId': 'pawn', 'needs': {'mood': .2, 'breakThresholdMinor': .3, 'food': .1, 'rest': .8, 'joy': .8},
              'dead': False, 'downed': False, 'drafted': False, 'jobPlayerForced': scenario == 'forced',
              'mentalState': 'Wander_Sad' if scenario == 'mental' else None}
    reference = mood_reference([person], [])
    state = {'Pawn': {'ID': 'pawn', 'Mood': .2, 'Threshold': .3}, 'Active': True, 'Missing': False,
             'Causes': [{'Need': 'food', 'Level': .1}]}
    reason = {'food': 'native_need_relief', 'forced': 'pawn_unavailable_or_player_work', 'mental': 'native_mental_break'}[scenario]
    method = {'Pawn': 'pawn', 'Need': 'food' if scenario == 'food' else '', 'Reason': reason,
              'Target': .5 if scenario == 'food' else 0, 'NeedBenefit': .4 if scenario == 'food' else None, 'MoodBenefit': None}
    active = {'review': {'Mood': {'States': [state]}, 'MoodMethods': [method]},
              'goals': {'EnsureMood-pawn': {'Need': 'deficit', 'Priority': 1 if scenario == 'mental' else 2}}}
    return active, reference, {'pawn': 'pawn', 'scenario': scenario}


@pytest.mark.parametrize('scenario', ['food', 'forced', 'mental'])
def test_mood_review_matches_native_python_reference(scenario):
    assert audit_mood_review(*mood_fixture(scenario))['states']['pawn']['Active']


@pytest.mark.parametrize('fault', ['recovered', 'priority', 'missing', 'wrong_need', 'forecast_benefit', 'guard'])
def test_mood_review_rejects_false_recovery_and_unsafe_proposals(fault):
    active, reference, setup = deepcopy(mood_fixture('forced' if fault == 'guard' else 'food'))
    state = active['review']['Mood']['States'][0]
    method = active['review']['MoodMethods'][0]
    if fault == 'recovered': active['goals']['EnsureMood-pawn']['Need'] = 'recovered'
    if fault == 'priority': active['goals']['EnsureMood-pawn']['Priority'] = 4
    if fault == 'missing': state['Missing'] = True
    if fault == 'wrong_need': state['Causes'][0]['Need'] = 'joy'
    if fault == 'forecast_benefit': method['MoodBenefit'] = .1
    if fault == 'guard': method['Need'] = 'food'
    with pytest.raises(AssertionError):
        audit_mood_review(active, reference, setup)


@pytest.mark.parametrize('fault', [None, 'advanced', 'clock_operation'])
def test_mood_hold_checks_paused_http_state_and_actual_clock_journal(tmp_path, fault):
    database = tmp_path / 'clock.sqlite'
    with sqlite3.connect(database) as db:
        db.execute('CREATE TABLE clock_attempts(id)')
        if fault == 'clock_operation': db.execute('INSERT INTO clock_attempts VALUES(1)')
    async def http(method, path):
        assert method == 'GET' and path == '/api/state'
        return {'connected': True, 'game': {'stale': False, 'paused': True, 'tick': 8 if fault == 'advanced' else 7}}
    if fault:
        with pytest.raises(AssertionError): asyncio.run(audit_mood_hold(http, database, 7))
    else:
        assert asyncio.run(audit_mood_hold(http, database, 7)) == {'tick': 7, 'clock_attempts': 0}
