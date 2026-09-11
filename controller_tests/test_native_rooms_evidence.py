"""Room acceptance rejects incomplete/native-mismatched facts without network use."""
import importlib.util
from pathlib import Path
import sys

import pytest

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("rooms_evidence", SCRIPTS / "native_rooms_acceptance.py")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
finally:
    sys.path.pop(0)


def sample():
    complete = {"page": {"complete": True}, "matched": "0", "returned": "0", "unreadable": "0"}
    shared = dict(role="None", properRoom=True, outdoors=False, psychologicallyOutdoors=False, touchesMapEdge=False, fogged=False, openRoofCount=0, cellCount=1)
    row = dict(shared, id="0", doorway=False, temperatureC=0, label="Room", contents=[], contentsCompleteness=complete,
               beds=[], pawns=[], stockpileZoneIds=[], stats=[], cells=[{"x": 1, "z": 1}], center={"x": 1, "z": 1},
               extents={"minimum": {"x": 1, "z": 1}, "maximum": {"x": 1, "z": 1}},
               cellsCompleteness=dict(complete, matched="1", returned="1"))
    native = dict(shared, id=0, isDoorway=False, temperature=0, gameLabel="Room", contentsNotListed=0, contents=[], bedCount=0,
                  beds=[], pawnCount=0, pawns=[], stockpiles=[], stats={}, cellsComplete=True, cellsNotListed=0, cells=[{"x": 1, "z": 1}])
    return row, native


def test_known_zero_and_false_room_facts_pass():
    row, native = sample()
    probe.compare_room(row, native, cells=True)


def test_native_temperature_comparison_accounts_only_for_declared_decimal_rounding():
    row, native = sample()
    row["temperatureC"], native["temperature"] = 4.6758575439453125, 4.7
    probe.compare_room(row, native, cells=True)
    row["temperatureC"] = 4.64
    with pytest.raises(AssertionError):
        probe.compare_room(row, native, cells=True)


@pytest.mark.parametrize("field", ["outdoors", "fogged", "openRoofCount", "temperatureC"])
def test_missing_room_facts_do_not_become_defaults(field):
    row, native = sample()
    del row[field]
    with pytest.raises(KeyError):
        probe.compare_room(row, native, cells=True)


@pytest.mark.parametrize("fault", ["duplicate", "missing", "center", "partial", "contents"])
def test_incomplete_geometry_and_census_cannot_pass(fault):
    row, native = sample()
    if fault == "duplicate": row["cells"].append(dict(row["cells"][0]))
    elif fault == "missing": row["cells"] = []
    elif fault == "center": row["center"] = {"x": 99, "z": 99}
    elif fault == "partial": row["cellsCompleteness"]["page"]["complete"] = False
    else: native["contentsNotListed"] = 1
    with pytest.raises((AssertionError, ValueError)):
        probe.compare_room(row, native, cells=True)
