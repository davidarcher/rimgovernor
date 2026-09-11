"""Pure evidence tests; no game, subprocess, network or HTTP listeners."""
import copy
import importlib.util
from pathlib import Path
import sys
import json
import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("emergency_acceptance", SCRIPTS / "native_emergency_service_acceptance.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)

CAPS = {name: {value} for name, value in {"identity": "rimgovernor/lifecycle_read_identity", "status": probe.STATUS,
    "control": probe.CONTROL, "execute": probe.EXECUTE, "preview": "rimgovernor/placement_preview",
    "fixture": "test/b04f_setup", "clock": "rimgovernor/clock_run_for"}.items()}


def events(*names):
    controls = 0
    result = []
    for index, name in enumerate(names):
        row = dict(Sequence=index + 11, OperationId=str(index), CapabilityId=name, EventType="operation.completed", Success=True, HasResult=True)
        if name == "control":
            row["Metadata"] = {"arguments": {"request": json.dumps({"acquire" if controls == 0 else "revoke": {}})}}
            controls += 1
        if name == "status": row["Metadata"] = {"arguments": {"request": json.dumps({"colonists": True, "threats": True})}}
        if name == "fixture": row["Metadata"] = {"arguments": {"op": "clear-opponents"}}
        result.append(row)
    return result


def test_unsafe_repeated_native_status_without_mutation():
    names = probe.audit(events("identity", "control", "preview", "status", "status", "control"), 10, CAPS, "unsafe")
    assert names.count(probe.STATUS) == 2 and probe.EXECUTE not in names


def test_safe_exactly_one_execute_between_explicit_controls():
    assert probe.audit(events("identity", "control", "status", "execute", "control"), 10, CAPS, "safe").count(probe.EXECUTE) == 1


@pytest.mark.parametrize("fault", ["gap", "duplicate", "unknown", "clock", "execute", "status-once", "status-failed", "status-no-result", "acquire-twice"])
def test_unsafe_cannot_hide_missing_evidence_or_effects(fault):
    rows = events("identity", "control", "preview", "status", "status", "control")
    if fault == "gap": rows.pop(2)
    elif fault == "duplicate": rows.append(dict(rows[-1]))
    elif fault in {"unknown", "clock", "execute"}: rows[2]["CapabilityId"] = fault
    elif fault == "status-once": rows[3]["CapabilityId"] = "preview"
    elif fault == "status-failed": rows[3]["Success"] = False
    elif fault == "status-no-result": rows[3]["HasResult"] = False
    else: rows[-1]["Metadata"]["arguments"]["request"] = '{"acquire":{}}'
    with pytest.raises(AssertionError): probe.audit(rows, 10, CAPS, "unsafe")


@pytest.mark.parametrize("names", [("identity", "control", "status", "control"),
    ("identity", "control", "execute", "status", "execute", "control"),
    ("identity", "execute", "control", "status", "control"),
    ("identity", "control", "status", "control", "execute")])
def test_safe_requires_single_ordered_effect(names):
    with pytest.raises(AssertionError): probe.audit(events(*names), 10, CAPS, "safe")


def test_orchestrator_can_only_clear_exact_fixture_scope():
    rows = events("identity", "status", "fixture", "preview")
    probe.audit(rows, 10, CAPS, "orchestrator")
    rows[2]["Metadata"]["arguments"]["op"] = "stocks"
    with pytest.raises(AssertionError): probe.audit(rows, 10, CAPS, "orchestrator")


def completion(count):
    return {"page": {"complete": True}, "matched": str(count), "returned": str(count), "unreadable": "0"}


def status(unsafe):
    context = {"identity": {"colonyId": "c", "loadToken": "l", "mapId": 0}, "tick": "1", "nativeGeneration": "2"}
    people = [dict(pawn=dict(id="pawn"), dead=False, downed=False, health=dict(bleeding=False, needsTend=False))]
    threats = [dict(pawn=dict(pawn=dict(id=name), dead=False, downed=False, hostile=True, mentalState="ManhunterPermanent")) for name in ("hare1", "hare2")] if unsafe else []
    return dict(context=context, colonists=dict(context=context, pawns=people, completeness=completion(1)),
        threats=dict(hostiles=threats, completeness=completion(len(threats))))


@pytest.mark.parametrize("unsafe", [False, True])
def test_complete_actual_healthy_threat_facts(unsafe):
    value = status(unsafe)
    probe.status_facts(value, value["context"]["identity"], 1, {"hare1", "hare2"}, unsafe)


@pytest.mark.parametrize("fault", ["missing-health", "bleeding", "downed", "empty-colonists", "incomplete", "missing-fixture", "wrong-tick", "wrong-map"])
def test_unknown_or_medical_hold_is_not_demonstrated_threat_gate(fault):
    value = status(True); identity = copy.deepcopy(value["context"]["identity"])
    if fault == "missing-health": del value["colonists"]["pawns"][0]["health"]["bleeding"]
    elif fault == "bleeding": value["colonists"]["pawns"][0]["health"]["bleeding"] = True
    elif fault == "downed": value["threats"]["hostiles"][0]["pawn"]["downed"] = True
    elif fault == "empty-colonists": value["colonists"].update(pawns=[], completeness=completion(0))
    elif fault == "incomplete": value["threats"]["completeness"]["page"]["complete"] = False
    elif fault == "missing-fixture": value["threats"]["hostiles"][0]["pawn"]["pawn"]["id"] = "other"
    elif fault == "wrong-tick": value["context"]["tick"] = "2"
    else: identity["mapId"] = 7
    with pytest.raises((AssertionError, KeyError)): probe.status_facts(value, identity, 1, {"hare1", "hare2"}, True)


def test_safe_rejects_remaining_standing_hostiles():
    value = status(True)
    with pytest.raises(AssertionError): probe.status_facts(value, value["context"]["identity"], 1, {"hare1", "hare2"}, False)


def test_pending_never_masks_started_attempt():
    submission = dict(planId="plan", actionId="action", revision="1", building={})
    progress = dict(stage="pending", attempt="0", unresolved=False, receipt=None)
    plan = dict(id="plan", revision="1", actions=[dict(id="action", building={}, progress=progress)])
    probe.pending(plan, submission)
    progress["attempt"] = "1"
    with pytest.raises(AssertionError): probe.pending(plan, submission)


def test_status_only_before_acquire_does_not_prove_enabled_worker_gate():
    with pytest.raises(AssertionError):
        probe.audit(events("identity", "status", "status", "control", "control"), 10, CAPS, "unsafe")


def test_status_missing_requested_threats_is_not_evidence():
    rows = events("identity", "control", "status", "status", "control")
    rows[2]["Metadata"]["arguments"]["request"] = '{"colonists":true,"threats":false}'
    with pytest.raises(AssertionError): probe.audit(rows, 10, CAPS, "unsafe")
