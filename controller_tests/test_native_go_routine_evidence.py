import copy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
try:
    import native_go_routine_acceptance as probe
finally:
    sys.path.pop(0)


def test_medical_care_native_evidence_uses_python_conditions_and_rejects_false_recovery():
    people = [{'thingId': 'patient', 'dead': False, 'health': {
        'careObservationVersion': 1, 'shouldSeekMedicalRest': False, 'needsTend': False,
        'hediffs': [{'isBad': True, 'defName': 'ChronicCondition'}]}}]
    expected = probe.medical_care_reference(people)
    assert expected == {'patients': ['patient'], 'unknown': [], 'need': 'deficit'}
    active = {'review': {'MedicalCare': {'CensusKnown': True, 'Patients': ['patient'], 'Unknown': None}},
              'goals': {'MaintainMedicalCare': {'Need': 'deficit', 'Priority': 2}}}
    probe.audit_medical_care(active, expected)
    for field, value in [('CensusKnown', False), ('Patients', []), ('Unknown', ['missing'])]:
        changed = copy.deepcopy(active)
        changed['review']['MedicalCare'][field] = value
        with pytest.raises(AssertionError): probe.audit_medical_care(changed, expected)
    active['goals']['MaintainMedicalCare']['Need'] = 'recovered'
    with pytest.raises(AssertionError): probe.audit_medical_care(active, expected)


def test_shell_geometry_requires_complete_perimeter_and_south_door():
    plan = {'actions': [{'building': {'x': x, 'z': z, 'stuff': 'WoodLog',
                                      'defName': 'Door' if (x, z) == (14, 20) else 'Wall'}}
                        for x in range(10, 19) for z in range(20, 29)
                        if x in (10, 18) or z in (20, 28)]}
    assert probe.shell_geometry(plan) == {(x, z) for x in range(11, 18) for z in range(21, 28)}
    for mutation in ['missing', 'duplicate', 'interior', 'door', 'stuff']:
        changed = copy.deepcopy(plan)
        if mutation == 'missing': changed['actions'].pop()
        if mutation == 'duplicate': changed['actions'][-1] = changed['actions'][0]
        if mutation == 'interior': changed['actions'][0]['building'].update(x=14, z=24)
        if mutation == 'door': changed['actions'][0]['building']['defName'] = 'Door'
        if mutation == 'stuff': changed['actions'][0]['building']['stuff'] = 'Steel'
        with pytest.raises(AssertionError): probe.shell_geometry(changed)


def test_operation_retention_rejects_history_loss_and_duplicate_pages():
    history = []
    probe.append_operation_history(history, [{'Sequence': 24}, {'Sequence': 25}], 23)
    probe.append_operation_history(history, [], 23)
    probe.append_operation_history(history, [{'Sequence': 26}], 23)
    for batch in [[{'Sequence': 26}], [{'Sequence': 28}], [{'Sequence': 28}, {'Sequence': 27}]]:
        with pytest.raises(AssertionError): probe.append_operation_history(history, batch, 23)
        assert [r['Sequence'] for r in history] == [24, 25, 26]


def test_native_operation_file_orders_events_and_preserves_gap_check(tmp_path):
    path = tmp_path / 'events.jsonl'
    path.write_text('{"Sequence":25}\n{"Sequence":23}\n{"Sequence":24}\n')
    assert [r['Sequence'] for r in probe.read_operation_history(path, 23)] == [24, 25]
    path.write_text('{"Sequence":25}\n')
    with pytest.raises(AssertionError): probe.read_operation_history(path, 23)


def test_routine_medical_evidence_preserves_care_and_unknowns():
    pawn = {"dead": False, "downed": False, "health": {"bleeding": False, "needsTend": True}}
    reply = {"observed": {"colonists": {"pawns": [pawn], "completeness": {
        "page": {"complete": True}, "matched": "1", "returned": "1", "filtered": "0", "unreadable": "0"}}}}
    assert probe.medical_need(reply) == "deficit"
    pawn["health"]["needsTend"] = False
    assert probe.medical_need(reply) == "recovered"
    del pawn["health"]["needsTend"]
    assert probe.medical_need(reply) == "unknown"
    pawn["dead"] = True
    assert probe.medical_need(reply) == "recovered"
    reply["observed"]["colonists"]["completeness"]["filtered"] = "1"
    assert probe.medical_need(reply) == "unknown"


def test_construction_start_accepts_omitted_empty_protobuf_hostiles():
    census = {'page': {'complete': True}, 'matched': '0', 'returned': '0', 'filtered': '0', 'unreadable': '0'}
    reply = {'observed': {'colonists': {'pawns': [], 'completeness': copy.deepcopy(census)},
                          'threats': {'completeness': copy.deepcopy(census)}}}
    probe.assert_construction_start(reply)
    for field in ('matched', 'returned', 'filtered', 'unreadable'):
        changed = copy.deepcopy(reply)
        changed['observed']['threats']['completeness'][field] = '1'
        with pytest.raises(AssertionError): probe.assert_construction_start(changed)
    reply['observed']['threats']['hostiles'] = [{'pawn': {}}]
    with pytest.raises(AssertionError): probe.assert_construction_start(reply)


def test_routine_trace_requires_attributed_native_reads():
    names = ["rimgovernor/observations_read_colony_facts", "rimgovernor/observations_read_status"]
    caps = {str(i): {name} for i, name in enumerate(names)}
    rows = [{"Sequence": i+1, "OperationId": str(i), "CapabilityId": str(i)} for i in range(2)]
    assert probe.audit_routine(rows, 0, caps, restart=False) == names
    for bad in [rows[1:], rows + [rows[-1]]]:
        with pytest.raises(AssertionError): probe.audit_routine(bad, 0, caps, restart=False)
    with pytest.raises(AssertionError): probe.audit_routine(rows, 0, caps, restart=True)
    for forbidden in ["rimgovernor/authority_control", "rimgovernor/clock_start", "rimgovernor/operations_execute", "home/supervised_play"]:
        changed = copy.deepcopy(caps)
        changed["0"] = {forbidden}
        with pytest.raises(AssertionError): probe.audit_routine(rows, 0, changed, restart=True)


def test_resource_rule_audit_allows_clock_but_rejects_construction():
    plan = {'actions': [{'progress': {'stage': 'pending', 'attempt': '0'}}]}
    names = ['rimgovernor/placement_preview', 'rimgovernor/clock_start',
             'rimgovernor/placement_preview', 'rimgovernor/clock_renew']
    probe.audit_resource_rules(plan, names)
    with pytest.raises(AssertionError):
        probe.audit_resource_rules(plan, names + [probe.EXECUTE])
    with pytest.raises(AssertionError):
        probe.audit_resource_rules(plan, names[:2])
    for changed in [{'actions': []}, {'actions': [{'progress': {'stage': 'pending', 'attempt': '1'}}]},
                    {'actions': [{'progress': {'stage': 'completed', 'attempt': '0'}}]}]:
        with pytest.raises(AssertionError):
            probe.audit_resource_rules(changed, names)


def test_development_audit_keeps_player_capacity_and_known_deficits():
    review = {'Snapshot': {'Plan': 'root'}, 'Tick': 10, 'Development': {
        'Snapshot': {'Plan': 'root'}, 'Tick': 10, 'Workers': 3, 'Capacity': 2,
        'Committed': ['player-project'], 'Rows': [
            {'Goal': 'EnsureBasicDefense', 'Deficit': 1.0, 'Score': 100.0,
             'WaitingSince': 10, 'Selected': True, 'Committed': False, 'Reason': ''}]}}
    assert probe.audit_development(review, 3) == review['Development']
    for field, value in [('Workers', None), ('Committed', []), ('Capacity', 3)]:
        changed = copy.deepcopy(review)
        changed['Development'][field] = value
        with pytest.raises(AssertionError):
            probe.audit_development(changed, 3)
    changed = copy.deepcopy(review)
    changed['Development']['Rows'][0]['Deficit'] = None
    with pytest.raises(AssertionError):
        probe.audit_development(changed, 3)
