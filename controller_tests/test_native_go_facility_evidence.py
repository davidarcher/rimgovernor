import copy
import json
from pathlib import Path
import sqlite3
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_facility_evidence import audit_facility_facts, completed_claims
finally:
    sys.path.pop(0)


def fixture():
    context = {'identity': {'mapId': 0}, 'tick': '12', 'nativeGeneration': '1'}
    cell = {'x': 3, 'z': 7}
    target = {'id': 'wall', 'shape': 'shape', 'missing': 1, 'excluded': 1, 'cells': [cell]}
    old = {'homeCoverage': {'revision': 2, 'targets': [target]}, 'structures': [{'id': 'wall', 'defName': 'Wall', 'flammability': 1.0}, {'id': 'player-wall', 'defName': 'Wall', 'flammability': 1.0}], 'version': 1, 'tick': 12}
    typed = {'homeCoverage': {'observed': {'revision': '2', 'targets': [{'id': 'wall', 'shapeToken': 'shape', 'missingCells': 1, 'excludedCells': 1, 'cells': [cell]}]}}, 'structures': [{'building': {'building': {'id': r['id']}}, 'flammability': r['flammability']} for r in old['structures']]}
    colony = {'context': context, 'upkeep': {'observed': typed}}
    legacy = {'success': True, 'tick': 12, 'upkeep': old}
    geometry = {'defName': 'Wall', 'x': 3, 'z': 7, 'rotation': 'north', 'stuff': 'WoodLog'}
    claims = [{'current': 'wall', 'building': geometry}]
    buildings = {'context': copy.deepcopy(context), 'buildings': [{'building': {'id': 'wall', 'defName': 'Wall', 'mapId': 0, 'position': cell}, 'status': 'built', 'rotation': 'North', 'stuff': 'WoodLog'}], 'completeness': {'page': {'complete': True}, 'matched': '1', 'returned': '1', 'unreadable': '0'}}
    return colony, legacy, buildings, claims


def test_facility_reference_requires_native_geometry_and_preserves_player_exclusions():
    assert audit_facility_facts(*fixture()) == {'home': ['wall'], 'stone': ['wall'], 'owned': ['wall']}


@pytest.mark.parametrize('kind', ['tick', 'replacement', 'material', 'unreadable', 'shape', 'excluded', 'flammability', 'unknown'])
def test_facility_reference_rejects_conflicting_evidence(kind):
    colony, legacy, buildings, claims = fixture()
    if kind == 'tick': buildings['context']['tick'] = '13'
    if kind == 'replacement': buildings['buildings'][0]['building']['id'] = 'new-wall'
    if kind == 'material': buildings['buildings'][0]['stuff'] = 'BlocksGranite'
    if kind == 'unreadable': buildings['completeness']['unreadable'] = '1'
    if kind == 'shape': colony['upkeep']['observed']['homeCoverage']['observed']['targets'][0]['shapeToken'] = 'other'
    if kind == 'excluded': colony['upkeep']['observed']['homeCoverage']['observed']['targets'][0]['excludedCells'] = 0
    if kind == 'flammability': colony['upkeep']['observed']['structures'][0]['flammability'] = 0
    if kind == 'unknown': colony['upkeep']['observed']['issues'] = [{'field': 'home_coverage'}]
    with pytest.raises((AssertionError, KeyError)):
        audit_facility_facts(colony, legacy, buildings, claims)


@pytest.mark.parametrize('kind', ['valid', 'missing-proof', 'pending', 'player', 'uncorrelated', 'duplicate-dispatch'])
def test_completed_claims_require_causal_autonomous_completion(tmp_path, kind):
    path = tmp_path / 'journal.sqlite'
    observation = {'Effect': 'completed', 'Causality': 'after_dispatch', 'Attempt': 1, 'Construction': {'Origin': 'blueprint', 'Current': 'wall'}}
    if kind == 'missing-proof': del observation['Construction']
    if kind == 'pending': observation['Effect'] = 'pending'
    if kind == 'uncorrelated': observation['Causality'] = ''
    with sqlite3.connect(path) as db:
        db.executescript('CREATE TABLE goals(id TEXT,payload TEXT); CREATE TABLE goal_methods(plan_id TEXT,goal_id TEXT); CREATE TABLE transitions(action_id TEXT,sequence INTEGER,payload TEXT);')
        db.execute('INSERT INTO goals VALUES(?,?)', ('goal', json.dumps({'Source': 'player' if kind == 'player' else 'autopilot', 'Status': 'invalidated'})))
        db.execute('INSERT INTO goal_methods VALUES(?,?)', ('plan', 'goal'))
        events = [{'Kind': 'dispatch'}, {'Kind': 'observe', 'Observation': observation}]
        if kind == 'duplicate-dispatch': events.append({'Kind': 'dispatch'})
        for sequence, event in enumerate(events): db.execute('INSERT INTO transitions VALUES(?,?,?)', ('action', sequence, json.dumps(event)))
    plans = [{'id': 'plan', 'actions': [{'id': 'action', 'building': fixture()[3][0]['building'], 'progress': {'stage': 'completed', 'effect': 'completed', 'attempt': '1', 'unresolved': False}}]}]
    if kind == 'valid':
        result = completed_claims(path, plans)
        assert result[0]['origin'] == 'blueprint' and result[0]['current'] == 'wall'
    else:
        with pytest.raises((AssertionError, KeyError)):
            completed_claims(path, plans)
