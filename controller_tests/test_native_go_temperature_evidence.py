from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_temperature_evidence import audit_temperature_outcome
finally:
    sys.path.pop(0)


def thermal_fixture(hot=False):
    cells = [{'x': x, 'z': z} for x in range(5) for z in range(5)]
    setup = {'beds': ['bed'], 'cells': cells, 'definition': 'PassiveCooler' if hot else 'Campfire'}
    room = {'id': 'room', 'properRoom': True, 'outdoors': False, 'psychologicallyOutdoors': False,
            'touchesMapEdge': False, 'openRoofCount': 0, 'cellCount': 25, 'cells': cells,
            'beds': [{'building': {'id': 'bed'}}], 'temperatureC': 22,
            'contents': [{'defName': setup['definition'], 'units': '1'}]}
    context = {'tick': '300'}
    rooms = {'observed': {'context': context.copy(), 'rooms': [room],
                         'completeness': {'page': {'complete': True}, 'matched': '1', 'returned': '1', 'unreadable': '0'}}}
    colony = {'observed': {'context': context.copy(), 'outdoorTemperatureC': 36 if hot else 5}}
    buildings = {'observed': {'context': context.copy(), 'buildings': [{'building': {'id': 'thermal', 'defName': setup['definition'], 'position': {'x': 2, 'z': 2}}, 'status': 'built'}]}}
    recovery = {'claims': [{'current': 'thermal', 'building': {'defName': setup['definition'], 'x': 2, 'z': 2}}]}
    return rooms, colony, buildings, setup, recovery


@pytest.mark.parametrize('hot', [False, True])
def test_temperature_outcome_requires_actual_room_recovery(hot):
    result = audit_temperature_outcome(*thermal_fixture(hot), 'hot' if hot else 'cold')
    assert result['temperature'] == 22 and result['completed'] == 'thermal'


@pytest.mark.parametrize('change', ['cold', 'hot', 'weather', 'open', 'wrong-bed', 'wrong-room', 'partial', 'duplicate', 'unowned', 'pending', 'stale'])
def test_temperature_rejects_receipts_and_unrelated_recovery(change):
    rooms, colony, buildings, setup, recovery = thermal_fixture()
    room = rooms['observed']['rooms'][0]
    if change == 'cold': room['temperatureC'] = 15
    if change == 'hot': room['temperatureC'] = 29
    if change == 'weather': colony['observed']['outdoorTemperatureC'] = 20
    if change == 'open': room['openRoofCount'] = 1
    if change == 'wrong-bed': room['beds'][0]['building']['id'] = 'other'
    if change == 'wrong-room': room['cells'] = [{'x': 99, 'z': 99}] * 25
    if change == 'partial': rooms['observed']['completeness']['page']['complete'] = False
    if change == 'duplicate': room['contents'][0]['units'] = '2'
    if change == 'unowned': recovery['claims'][0]['current'] = 'other'
    if change == 'pending': buildings['observed']['buildings'][0]['status'] = 'blueprint'
    if change == 'stale': buildings['observed']['context']['tick'] = '299'
    with pytest.raises((AssertionError, KeyError)):
        audit_temperature_outcome(rooms, colony, buildings, setup, recovery, 'cold')
