import copy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
try:
    import native_go_routine_acceptance as probe
finally:
    sys.path.pop(0)


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
