from copy import deepcopy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from native_draft_acceptance import OWNER, pawn_row, target, execute_request, release_request, same_control, owned_effect, actual_order


def reply():
    context = {"identity": {"colonyId": "colony", "loadToken": "load", "mapId": 0}, "tick": "0", "nativeGeneration": "1"}
    snapshot = {"context": deepcopy(context), "entityId": "Human1", "token": "token"}
    row = {"pawn": {"id": "Human1", "snapshot": snapshot}, "drafted": True,
        "draftClaim": {"owned": {"claimId": "claim", "owner": deepcopy(OWNER), "pawnSnapshot": deepcopy(snapshot)}}}
    return {"observed": {"context": context, "pawns": [row], "completeness": {
        "page": {"complete": True}, "matched": "1", "returned": "1", "unreadable": "0"}}}


def row():
    value = reply()
    return pawn_row(value, value["observed"]["context"]["identity"], "Human1")


def test_exact_snapshot_and_claim_validation():
    value = reply()
    assert pawn_row(value, value["observed"]["context"]["identity"], "Human1")["drafted"] is True
    with pytest.raises(AssertionError):
        pawn_row(value, value["observed"]["context"]["identity"], "Human2")


@pytest.mark.parametrize("mutation", ["token", "entity", "context", "claim_snapshot", "unavailable", "boolean", "count", "page"])
def test_readback_rejects_partial_or_fabricated_correlation(mutation):
    value = reply(); observed = value["observed"]; pawn = observed["pawns"][0]
    if mutation == "token": pawn["pawn"]["snapshot"]["token"] = ""
    elif mutation == "entity": pawn["pawn"]["snapshot"]["entityId"] = "other"
    elif mutation == "context": pawn["pawn"]["snapshot"]["context"]["tick"] = "1"
    elif mutation == "claim_snapshot": pawn["draftClaim"]["owned"]["pawnSnapshot"]["token"] = "stale"
    elif mutation == "unavailable": pawn["draftClaim"] = {"unavailable": {}}
    elif mutation == "boolean": pawn["drafted"] = 1
    elif mutation == "count": observed["completeness"]["matched"] = "2"
    elif mutation == "page": observed["completeness"]["page"]["complete"] = False
    with pytest.raises(AssertionError):
        pawn_row(value, observed["context"]["identity"], "Human1")


def test_request_uses_exact_native_token_and_original_owner():
    pawn = row(); identity = pawn["pawn"]["snapshot"]["context"]["identity"]
    grant = {"context": {"nativeGeneration": "9"}, "leaseId": "lease"}
    request = execute_request(identity, grant, pawn, 7)
    assert request["operation"]["setDrafted"] == {"pawn": target(pawn), "drafted": True, "allowPersistentDraft": False}
    assert request["precondition"]["expectedGeneration"] == "9"
    cleanup = release_request(identity, pawn)
    pawn["draftClaim"]["owned"]["owner"]["playerDirection"] = "2"
    assert cleanup["originalOwner"] == OWNER
    with pytest.raises(AssertionError):
        release_request(identity, pawn)


@pytest.mark.parametrize("field", ["token", "claim", "drafted"])
def test_replay_check_rejects_a_second_transition(field):
    before = row(); after = deepcopy(before)
    if field == "token": after["pawn"]["snapshot"]["token"] = "replacement"
    elif field == "claim": after["draftClaim"]["owned"]["claimId"] = "replacement"
    else: after["drafted"] = False
    with pytest.raises(AssertionError): same_control(before, after)


def test_no_change_must_be_verified_without_issuing_setter():
    pawn = row()
    effect = {"pawnId": "Human1", "drafted": True, "verified": True, "issued": False,
        "draftClaimId": "claim", "draftOwner": OWNER["controllerSessionId"], "resultingSnapshotToken": "token"}
    receipt = {"noChange": {"observed": {"job": effect}}}
    owned_effect(receipt, pawn, "noChange", False)
    effect["issued"] = True
    with pytest.raises(AssertionError): owned_effect(receipt, pawn, "noChange", False)


def test_completed_move_compares_actual_current_job_without_inventing_goto():
    external = {"success": True, "accepted": True, "jobId": 58, "jobDef": "Wait_Combat"}
    pawn = {"job": {"loadId": "58", "defName": "Wait_Combat"}}
    actual_order(external, pawn)
    for wrong in ({"loadId": "59", "defName": "Wait_Combat"}, {"loadId": "58", "defName": "Goto"}):
        with pytest.raises(AssertionError): actual_order(external, {"job": wrong})
    external["accepted"] = False
    with pytest.raises(AssertionError): actual_order(external, pawn)


def test_native_no_current_job_requires_absence():
    external = {"success": True, "accepted": True, "jobId": None, "jobDef": None}
    actual_order(external, {"job": {"playerForced": False, "queuedJobs": 0}})
    with pytest.raises(AssertionError): actual_order(external, {"job": {"playerForced": True}})
    with pytest.raises(AssertionError): actual_order(external, {"job": {"defName": "Goto"}})
