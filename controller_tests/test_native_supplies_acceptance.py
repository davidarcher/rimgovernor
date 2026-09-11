"""Reject false census completeness/zeros; native execution remains separate."""
import copy
import importlib.util
from pathlib import Path
import sys

import pytest

SCRIPTS = Path(__file__).parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("native_supplies_acceptance", SCRIPTS / "native_supplies_acceptance.py")
    acceptance = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(acceptance)
finally:
    sys.path.pop(0)


def complete(number):
    return {"page": {"complete": True}, "matched": str(number), "returned": str(number), "unreadable": "0"}


def samples():
    legacy = dict(defName="WoodLog", total=5, stacks=2, ours=2, oursUnforbidden=2, forbidden=0,
        otherFaction=3, fogged=0, reserved=0, inStockpile=2, inHomeArea=5, carried=3, inContainer=0, traderStock=3)
    row = {key: str(value) for key, value in legacy.items() if type(value) is int}
    row.pop("total")
    row.update(definition={"defName": "WoodLog"}, units="5", spawned="2", playerFaction="0",
        items=[{"id": "wood1", "mapId": 0}, {"id": "wood2", "mapId": 0}],
        holders=[{"holder": {"id": "muffalo1"}, "units": "3"}],
        itemsCompleteness=complete(2), holdersCompleteness=complete(1), corpsesCompleteness=complete(0))
    return row, legacy


def test_census_requires_actual_quantities_and_complete_instances():
    row, legacy = samples()
    acceptance.check_stock(row, legacy, {"mapId": 0}, include_held=True)
    for field, value in (("units", "0"), ("carried", "0"), ("ours", "5"), ("items", []),
                         ("holders", []), ("itemsCompleteness", complete(0))):
        changed = copy.deepcopy(row)
        changed[field] = value
        with pytest.raises(AssertionError):
            acceptance.check_stock(changed, legacy, {"mapId": 0}, include_held=True)
    missing = copy.deepcopy(row)
    del missing["carried"]
    with pytest.raises(KeyError):
        acceptance.check_stock(missing, legacy, {"mapId": 0}, include_held=True)


def test_excluded_held_scope_cannot_claim_known_empty():
    row, legacy = samples()
    for key in ("carried", "inContainer", "traderStock"):
        legacy[key] = 0
        row.pop(key)
    legacy.update(total=2, stacks=1, otherFaction=0, inHomeArea=2)
    row.update(units="2", stacks="1", otherFaction="0", inHomeArea="2", items=row["items"][:1], holders=[],
        itemsCompleteness=complete(1), holdersCompleteness={"page": {"complete": False}},
        issues=[{"field": field, "unavailable": {"reason": "UNAVAILABLE_REASON_NOT_REQUESTED"}}
                for field in ("carried", "in_container", "trader_stock")])
    acceptance.check_stock(row, legacy, {"mapId": 0}, include_held=False)
    row["carried"] = "0"
    with pytest.raises(AssertionError):
        acceptance.check_stock(row, legacy, {"mapId": 0}, include_held=False)


@pytest.mark.parametrize("invalid", [None, False, 0, -1, "-1", "01", "1.0", "١", "9223372036854775808"])
def test_missing_or_noncanonical_counts_are_not_zero(invalid):
    with pytest.raises(AssertionError):
        acceptance.count(invalid)
