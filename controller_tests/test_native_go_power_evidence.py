from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_power_evidence import audit_power_outcome
finally:
    sys.path.pop(0)


def power_fixture():
    def building(id, definition, x, output):
        return {'building': {'id': id, 'defName': definition, 'position': {'x': x, 'z': 2}},
                'service': {'connected': True, 'powerOn': True, 'powerNetId': 'network', 'powerOutputW': output}}
    consumer = building('lamp', 'StandingLamp', 2, -30)
    producer = building('generator', 'WoodFiredGenerator', 5, 1000)
    development = {'power': [{'baseW': -30, 'building': consumer}, {'baseW': 1000, 'building': producer}],
                   'completeness': {'page': {'complete': True}, 'matched': '2', 'returned': '2', 'unreadable': '0'}}
    reply = {'observed': {'context': {'tick': '200'}, 'development': {'observed': development}}}
    setup = {'consumer': 'lamp'}
    recovery = {'claims': [{'current': 'generator', 'building': {'defName': 'WoodFiredGenerator', 'x': 5, 'z': 2}}]}
    return reply, setup, recovery


def test_power_outcome_requires_native_service_and_owned_generation():
    result = audit_power_outcome(*power_fixture(), 'generation')
    assert result['consumer'] == 'lamp' and result['completed'] == ['generator']


@pytest.mark.parametrize('change', ['unpowered', 'unfueled', 'foreign', 'duplicate', 'geometry', 'unowned', 'partial'])
def test_power_outcome_rejects_receipts_and_unrelated_supply(change):
    reply, setup, recovery = power_fixture()
    development = reply['observed']['development']['observed']
    consumer, producer = [r['building'] for r in development['power']]
    if change == 'unpowered': consumer['service']['powerOn'] = False
    if change == 'unfueled': producer['service']['powerOutputW'] = 0
    if change == 'foreign': producer['service']['powerNetId'] = 'other'
    if change == 'duplicate': development['power'].append(development['power'][1])
    if change == 'geometry': producer['building']['position']['x'] = 6
    if change == 'unowned': recovery['claims'] = []
    if change == 'partial': development['completeness']['page']['complete'] = False
    with pytest.raises((AssertionError, KeyError)):
        audit_power_outcome(reply, setup, recovery, 'generation')
