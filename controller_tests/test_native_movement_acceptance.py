from copy import deepcopy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from native_movement_acceptance import candidates, job_effect, arrival, move_request


def cells():
    rows = [{"cell": {"x": x, "z": 0}, "terrain": "Soil", "walkable": True, "passable": True, "fogged": False} for x in (0, 2, 3, 6)]
    return {"cells": rows, "completeness": {"page": {"complete": True}, "matched": "4", "returned": "4", "unreadable": "0"}}


def test_candidates_bounded_and_observed_traversal_required():
    value = cells()
    assert candidates(value, {"x": 0, "z": 0}) == [{"x": 2, "z": 0}, {"x": 3, "z": 0}]
    for field, bad in (("walkable", False), ("passable", False), ("fogged", True), ("walkable", 1)):
        changed = deepcopy(value); changed["cells"][1][field] = bad
        assert candidates(changed, {"x": 0, "z": 0}) == [{"x": 3, "z": 0}]


def test_partial_cells_cannot_prove_safe_destination():
    value = cells(); value["completeness"]["unreadable"] = "1"
    with pytest.raises(AssertionError): candidates(value, {"x": 0, "z": 0})


def fixture():
    destination = {"x": 2, "z": 0}
    effect = {"pawnId": "Human1", "jobId": 58, "jobDef": "Goto", "issued": True, "verified": True,
        "targetA": {"cell": destination}, "draftClaimId": "claim"}
    row = {"pawn": {"id": "Human1", "position": destination, "snapshot": {"token": "native"}},
        "job": {"loadId": "58", "defName": "Goto"}, "draftClaim": {"owned": {"claimId": "claim"}}}
    progress = {"completeInspection": True, "completed": {"evidence": {"job": deepcopy(effect)}}}
    return destination, effect, row, progress


def test_receipt_requires_exact_live_job_identity():
    destination, effect, row, _ = fixture()
    job_effect({"applied": {"observed": {"job": effect}}}, row, destination)
    row["job"]["loadId"] = "59"
    with pytest.raises(AssertionError): job_effect({"applied": {"observed": {"job": effect}}}, row, destination)


@pytest.mark.parametrize("bad", ["position", "pending", "inspection", "job", "claim", "target"])
def test_arrival_requires_position_and_causal_completed_evidence(bad):
    destination, effect, row, progress = fixture()
    arrival(progress, row, destination, effect)
    if bad == "position": row["pawn"]["position"] = {"x": 1, "z": 0}
    elif bad == "pending": progress["pending"] = progress.pop("completed")
    elif bad == "inspection": progress["completeInspection"] = False
    elif bad == "job": progress["completed"]["evidence"]["job"]["jobId"] = 59
    elif bad == "claim": row["draftClaim"]["owned"]["claimId"] = "replacement"
    else: progress["completed"]["evidence"]["job"]["targetA"]["cell"] = {"x": 3, "z": 0}
    with pytest.raises(AssertionError): arrival(progress, row, destination, effect)


def test_move_request_uses_native_snapshot_and_immutable_destination():
    destination, _, row, _ = fixture()
    request = move_request({"mapId": 0}, {"context": {"nativeGeneration": "2"}, "leaseId": "lease"}, row, 4, destination)
    assert request["operation"] == {"movePawn": {"pawn": {"entityId": "Human1", "expectedSnapshotToken": "native"}, "destination": {"x": 2, "z": 0}}}
    destination["x"] = 9
    assert request["operation"]["movePawn"]["destination"]["x"] == 2
