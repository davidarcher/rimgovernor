from copy import deepcopy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from native_pawn_acceptance import rows, compare_core


def snapshot():
    return {"context": {"tick": "0"}, "pawns": [], "completeness": {
        "page": {"complete": True}, "matched": "0", "returned": "0", "filtered": "4", "unreadable": "0"}}


def test_empty_requires_complete_zero_metadata():
    assert rows(snapshot(), {"tick": "0"}) == []
    for field, value in (("matched", "1"), ("returned", "1"), ("unreadable", "1")):
        bad = snapshot(); bad["completeness"][field] = value
        with pytest.raises(AssertionError):
            rows(bad)


@pytest.mark.parametrize("page", [{"complete": False}, {"complete": True, "nextCursor": "x"}])
def test_incomplete_or_cursor_never_counts_as_whole_query(page):
    bad = snapshot(); bad["completeness"]["page"] = page
    with pytest.raises(AssertionError):
        rows(bad)


def test_context_mismatch_fails():
    with pytest.raises(AssertionError):
        rows(snapshot(), {"tick": "1"})


def test_core_comparison_rejects_missing_pawns_and_false_coercion():
    old = {"thingId": "Human1", "defName": "Human", "kindDef": "Colonist", "position": {"x": 0, "z": 0},
        "isColonist": True, "isFreeColonist": True, "isPrisoner": False,
        **{key: False for key in ("animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted")}}
    new = {"pawn": {"id": "Human1", "defName": "Human", "position": {"x": 0, "z": 0}}, "kindDefName": "Colonist",
        "colonist": True, "freeColonist": True, "prisoner": False,
        **{key: False for key in ("animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted")}}
    compare_core([new], [old])
    with pytest.raises(AssertionError):
        compare_core([], [old])
    bad = deepcopy(new); bad["drafted"] = 0
    with pytest.raises(AssertionError):
        compare_core([bad], [old])


def controlled_snapshot(owned=False):
    value = snapshot()
    value["context"] = {"identity": {"colonyId": "c", "loadToken": "l", "mapId": 0}, "tick": "1", "nativeGeneration": "2"}
    ref = {"context": deepcopy(value["context"]), "entityId": "Thing_Human42", "token": "opaque-native-token"}
    claim = {"unowned": {}}
    if owned:
        claim = {"owned": {"claimId": "claim", "owner": {"controllerSessionId": "original owner", "playerDirection": "3"}, "pawnSnapshot": deepcopy(ref)}}
    value["pawns"] = [{"pawn": {"id": "Thing_Human42", "mapId": 0, "snapshot": ref}, "colonist": True, "dead": False,
        "animal": False, "drafted": owned, "draftClaim": claim, "issues": []}]
    value["completeness"].update(matched="1", returned="1")
    return value


@pytest.mark.parametrize("owned", [False, True])
def test_live_colonist_exact_draft_control_snapshot(owned):
    value = controlled_snapshot(owned)
    assert rows(value, value["context"]) == value["pawns"]


@pytest.mark.parametrize("fault", ["missing-snapshot", "missing-claim", "wrong-pawn", "wrong-context", "empty-token", "invalid-unicode", "stale-issue", "health-cas", "settings-cas"])
def test_live_colonist_snapshot_cannot_silently_disappear_or_change_scope(fault):
    value = controlled_snapshot(); row = value["pawns"][0]
    if fault == "missing-snapshot": del row["pawn"]["snapshot"]
    elif fault == "missing-claim": del row["draftClaim"]
    elif fault == "wrong-pawn": row["pawn"]["snapshot"]["entityId"] = "other"
    elif fault == "wrong-context": row["pawn"]["snapshot"]["context"]["tick"] = "2"
    elif fault == "empty-token": row["pawn"]["snapshot"]["token"] = " "
    elif fault == "invalid-unicode": row["pawn"]["snapshot"]["token"] = "\ud800"
    elif fault == "stale-issue": row["issues"] = [{"field": "pawn.snapshot"}]
    else: row[fault.split("-")[0]] = {"snapshot": {}}
    with pytest.raises((AssertionError, KeyError)): rows(value)


@pytest.mark.parametrize("fault", ["empty-id", "different-snapshot", "undrafted", "empty-owner", "zero-direction", "leading-zero", "overflow", "integer-direction", "two-variants"])
def test_owned_claim_requires_exact_native_binding_and_original_owner(fault):
    value = controlled_snapshot(True); row = value["pawns"][0]; claim = row["draftClaim"]["owned"]
    if fault == "empty-id": claim["claimId"] = ""
    elif fault == "different-snapshot": claim["pawnSnapshot"]["token"] = "other"
    elif fault == "undrafted": row["drafted"] = False
    elif fault == "empty-owner": claim["owner"]["controllerSessionId"] = " "
    elif fault == "two-variants": row["draftClaim"]["unowned"] = {}
    else: claim["owner"]["playerDirection"] = {"zero-direction": "0", "leading-zero": "03", "overflow": "18446744073709551616", "integer-direction": 3}[fault]
    with pytest.raises(AssertionError): rows(value)


def animal_snapshot():
    value = controlled_snapshot(); row = value["pawns"][0]
    del row["pawn"]["snapshot"]
    row.update(colonist=False, animal=True)
    unavailable = {"reason": "UNAVAILABLE_REASON_NOT_APPLICABLE", "detail": "No native draft controller"}
    row["draftClaim"] = {"unavailable": unavailable}
    row["issues"] = [{"field": "pawn.snapshot", "unavailable": deepcopy(unavailable)}]
    return value


def test_animal_without_drafter_has_explicit_unavailability_and_no_snapshot():
    assert rows(animal_snapshot())


@pytest.mark.parametrize("fault", ["colonist", "snapshot-present", "legacy-unsupported", "missing-issue"])
def test_unavailability_cannot_mask_integrated_live_colonist_or_invent_cas(fault):
    value = animal_snapshot(); row = value["pawns"][0]
    if fault == "colonist": row.update(colonist=True, animal=False)
    elif fault == "snapshot-present": row["pawn"]["snapshot"] = {}
    elif fault == "legacy-unsupported": row["draftClaim"]["unavailable"]["reason"] = "UNAVAILABLE_REASON_UNSUPPORTED"
    else: row["issues"] = []
    with pytest.raises(AssertionError): rows(value)
