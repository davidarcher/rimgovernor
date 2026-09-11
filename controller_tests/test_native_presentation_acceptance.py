"""Presentation acceptance must reject partial or altered native observations."""
import importlib.util
from pathlib import Path
import sys
import pytest

SCRIPTS = Path(__file__).parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))
try:
    spec = importlib.util.spec_from_file_location("presentation_acceptance", SCRIPTS / "native_presentation_acceptance.py")
    acceptance = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(acceptance)
finally:
    sys.path.pop(0)


def test_listing_preserves_known_empty_and_refuses_missing_counts():
    acceptance.listing(dict(totalCount=0, returnedCount=0, complete=True, truncated=False), 0)
    with pytest.raises(AssertionError):
        acceptance.listing(dict(complete=True), 0)


@pytest.mark.parametrize("change", [{"totalCount": 2}, {"returnedCount": 0}, {"complete": False}, {"truncated": True}])
def test_listing_refuses_partial(change):
    with pytest.raises(AssertionError):
        acceptance.listing(dict(totalCount=1, returnedCount=1, complete=True, truncated=False) | change, 1)


def test_camera_signed_viewport_and_false_extension_are_facts():
    native = dict(success=True, rootSize=20, zoomRootSize=20, sizeRange=dict(min=10, max=30),
        mapPosition=dict(x=0, z=5), zoomRange="Close", cameraZoomExtensionEnabled=False,
        viewRect=dict(minX=-3, minZ=-2, maxX=10, maxZ=20))
    typed = dict(rootSize=20, zoomRootSize=20, minimumRootSize=10, maximumRootSize=30,
        mapPosition=dict(z=5), nativeZoomRange="Close", zoomExtensionEnabled=False, viewRect=native["viewRect"].copy())
    acceptance.camera_matches(typed, native)
    typed["viewRect"]["minX"] = 0
    with pytest.raises(AssertionError): acceptance.camera_matches(typed, native)


def test_roster_requires_actual_exact_ids_and_spawned_positions():
    native = dict(success=True, count=1, colonists=[dict(pawnId="Thing_17", name="Pawn", spawned=True, position=dict(x=0,z=2))])
    typed = dict(listing=dict(totalCount=1, returnedCount=1, complete=True, truncated=False),
        colonists=[dict(pawnId="Thing_17", name="Pawn", spawned=True, mapId=7, position=dict(z=2))])
    acceptance.roster_matches(typed, native, dict(mapId=7))
    typed["colonists"][0]["pawnId"] = "17"
    with pytest.raises(AssertionError): acceptance.roster_matches(typed, native, dict(mapId=7))


def test_selected_pawn_label_and_exact_native_facts():
    native = dict(success=True, selectedCount=1, selectedObjects=[dict(id="Thing_Human17", kind="pawn", type="Verse.Pawn", label="Short",
        details=dict(defName="Human", position=dict(x=2,z=3)))])
    typed = dict(listing=dict(totalCount=1, returnedCount=1, complete=True, truncated=False), selectedObjects=[
        dict(id="Thing_Human17", nativeKind="pawn", nativeType="Verse.Pawn", label="Short", defName="Human", mapId=0, position=dict(x=2,z=3))])
    acceptance.selection_matches(typed, native, "Thing_Human17", dict(mapId=0))
    typed["selectedObjects"][0]["label"] = "Short, Full title"
    with pytest.raises(AssertionError): acceptance.selection_matches(typed, native, "Thing_Human17", dict(mapId=0))
