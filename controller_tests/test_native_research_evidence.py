"""Native research evidence must distinguish missing facts from zero/false."""
import copy
import importlib.util
from pathlib import Path
import sys

import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("research_evidence", SCRIPTS / "native_research_acceptance.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)


def capability():
    row = {"intellectual": 5, "priority": 0, "disabled": False, "everWork": True, "active": False}
    return ({"researchers": [dict(row, pawn={"id": "Thing_Pawn1"})]},
            {"researchBenches": {"readable": True, "count": 0, "benches": [], "researchers": [dict(row, thingId="Pawn1")]}})


def test_fingerprint_compares_native_state_not_distinct_sdk_operation_metadata():
    before = dict(success=True, current=None, progress=[], knowledge=[], slots=None, techprints=[], tick=1, paused=True, operation={"OperationId": "first"})
    after = dict(before, operation={"OperationId": "second"})
    assert probe.fingerprint_state(before) == probe.fingerprint_state(after)
    after["slots"] = []
    assert probe.fingerprint_state(before) != probe.fingerprint_state(after)
    del after["progress"]
    with pytest.raises(KeyError):
        probe.fingerprint_state(after)


def test_known_zero_research_priority_is_valid():
    probe.compare_capability(*capability())


@pytest.mark.parametrize("field", ["intellectual", "priority", "disabled", "everWork", "active"])
def test_missing_researcher_facts_cannot_imply_zero_or_false(field):
    observed, legacy = capability()
    observed["researchers"][0].pop(field)
    with pytest.raises(AssertionError):
        probe.compare_capability(observed, legacy)


def test_finished_census_requires_complete_native_set_and_closed_gate():
    observed = {"projects": [{"project": {"defName": "Finished"}, "finished": True, "canStart": False, "available": False}],
                "completeness": {"page": {"complete": True}, "matched": "1", "returned": "1", "unreadable": "0"},
                "slots": [{}], "anomalyActive": False}
    legacy = {"available": [], "locked": [], "finished": ["Finished"], "current": None, "currentByCategory": {}, "anomalyActive": False}
    probe.compare_projects(observed, legacy)
    for field in ("canStart", "available"):
        bad = copy.deepcopy(observed)
        bad["projects"][0][field] = True
        with pytest.raises(AssertionError):
            probe.compare_projects(bad, legacy)
    legacy["finished"].append("Missing")
    with pytest.raises(AssertionError):
        probe.compare_projects(observed, legacy)
