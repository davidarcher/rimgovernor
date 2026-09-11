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
