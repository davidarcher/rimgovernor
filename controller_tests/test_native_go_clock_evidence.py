"""Acceptance evidence must distinguish Go orchestration from protocol receipts."""
import copy
import json
import sqlite3
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
try:
    import native_go_clock_acceptance as probe
finally:
    sys.path.pop(0)


def trace(*names):
    caps = {name: {"rimgovernor/" + name} for name in names}
    rows = [{"Sequence": i + 1, "OperationId": str(i), "CapabilityId": name} for i, name in enumerate(names)]
    return rows, caps


@pytest.mark.parametrize("name", ["clock_start", "clock_renew", "operations_execute", "authority_control"])
def test_disabled_restart_cannot_hide_writes(name):
    rows, caps = trace("clock_read_events", name)
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=True)


def test_go_clock_trace_requires_contiguous_attributed_control():
    rows, caps = trace("clock_read_events", "clock_start", "operations_execute", "clock_pause")
    assert len(probe.audit(rows, 0, caps, restart=False)) == 4
    for bad in (rows[1:], rows + [rows[-1]]):
        with pytest.raises(AssertionError):
            probe.audit(bad, 0, caps, restart=False)
    caps["clock_start"] = {"home/supervised_play"}
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=False)


def test_interruption_requires_exact_native_letter_stop():
    event = {"stopped": {"reason": "STOP_REASON_LETTER_PAUSE", "pause": {"letter": {"id": "Letter_1"}}}}
    assert probe.interrupted_by_letter([event], "Letter_1") == event
    changed = copy.deepcopy(event)
    changed["stopped"]["reason"] = "STOP_REASON_REQUESTED_PAUSE"
    for values in ([], [changed], [event, event]):
        with pytest.raises(AssertionError):
            probe.interrupted_by_letter(values, "Letter_1")
    with pytest.raises(AssertionError):
        probe.interrupted_by_letter([event], "Letter_2")


def test_routine_trace_requires_read_and_rejects_it_on_disabled_restart():
    rows, caps = trace("clock_read_events", "clock_start", "operations_execute", "clock_pause")
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=False, routine_reviews=True)
    rows, caps = trace("clock_read_events", "clock_start", "operations_execute", "clock_pause", "observations_read_colony_facts")
    assert len(probe.audit(rows, 0, caps, restart=False, routine_reviews=True)) == 5
    rows, caps = trace("clock_read_events", "observations_read_colony_facts")
    with pytest.raises(AssertionError):
        probe.audit(rows, 0, caps, restart=True, routine_reviews=True)


@pytest.mark.parametrize("field", [None, "dead", "downed", "bleeding", "needsTend", "unknown"])
def test_clock_fixture_reports_medical_prerequisite_before_running(field):
    pawn = {"pawn": {"id": "pawn"}, "dead": False, "downed": False,
            "health": {"bleeding": False, "needsTend": False}}
    if field in {"dead", "downed"}: pawn[field] = True
    elif field in {"bleeding", "needsTend"}: pawn["health"][field] = True
    elif field == "unknown": del pawn["health"]["needsTend"]
    reply = {"observed": {"colonists": {"pawns": [pawn], "completeness": {
        "page": {"complete": True}, "matched": "1", "returned": "1", "filtered": "0", "unreadable": "0"}}}}
    if field:
        with pytest.raises(AssertionError, match="prerequisite.*pawn"):
            probe.require_healthy_colonists(reply)
    else: probe.require_healthy_colonists(reply)


@pytest.mark.parametrize("fault", [None, "food", "world", "missing", "active", "method"])
def test_routine_evidence_requires_native_scope_unknown_forecast_and_manual_retirement(tmp_path, fault):
    path = tmp_path / "review.sqlite"
    needs = ["ConfirmColonyNames", "ActiveCombat", "CriticalMedical", "RestoreWorkers", "AllowStartingSupplies",
             "EnsureWorkAssignments", "EnsureFoodSupply", "EnsureInitialShelter", "EnsureTemperatureSafety",
             "EnsureCooking", "EnsureBasicPower", "EnsureFoodStorage", "EnsureBasicDefense", "MaintainWood", "MaintainMedicalCare", "EnsureComfort", "EnsureExpansion", "MaintainEquipment",
             "MaintainFireSafety", "SecureSupplies", "MaintainEssentialRepairs", "MaintainCleanFacilities"]
    scope = {"Colony": "colony", "Load": "load", "Map": 0}
    review = {"Revision": 2, "Enabled": False, "Snapshot": scope, "Tick": 7,
              "Goals": [{"Need": name, "Goal": name} for name in needs]}
    with sqlite3.connect(path) as db:
        db.executescript("CREATE TABLE routine_review(singleton,payload); CREATE TABLE goals(id,payload); CREATE TABLE goal_methods(id);")
        db.execute("INSERT INTO routine_review VALUES(1,?)", (json.dumps(review),))
        for name in needs:
            goal = {"Source": "autopilot", "Snapshot": dict(scope), "Tick": 7, "Status": "invalidated", "Need": "unknown"}
            if name == "EnsureFoodSupply":
                if fault == "food": goal["Need"] = "recovered"
                if fault == "world": goal["Snapshot"]["Load"] = "other"
                if fault == "active": goal["Status"] = "active"
                if fault == "missing": continue
            db.execute("INSERT INTO goals VALUES(?,?)", (name, json.dumps(goal)))
        if fault == "method": db.execute("INSERT INTO goal_methods VALUES(1)")
    identity = {"colonyId": "colony", "loadToken": "load", "mapId": 0}
    if fault:
        with pytest.raises(AssertionError): probe.routine_evidence(path, identity, enabled=False, expected_food_need="unknown")
    else:
        assert len(probe.routine_evidence(path, identity, enabled=False, expected_food_need="unknown")["goals"]) == len(needs)
