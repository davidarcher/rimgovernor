from rimbot.room_outcomes import assess_interior
from rimbot.project_outcomes import assess


def room(**changes):
    return dict(id=42, visible_cells=[{'x': 1, 'z': 1}, {'x': 2, 'z': 1}],
                touches_map_edge=False, is_doorway=False, open_roof_count=0,
                pawns_reaching_visible_cell=[10], **changes)


def test_roofed_outdoor_area_does_not_complete_shelter():
    native = room(); native['touches_map_edge'] = True
    evidence = assess_interior({(1, 1)}, [native])
    result = assess({'kind': 'construction', 'progress': {'enclosures': [evidence]}})
    assert result['status'] == 'unmet'
    assert evidence['roofed'] and not evidence['enclosed']


def test_sealed_inaccessible_room_does_not_complete_shelter():
    native = room(); native['pawns_reaching_visible_cell'] = []
    evidence = assess_interior({(1, 1)}, [native])
    assert evidence['enclosed'] and not evidence['reachable']


def test_partial_observation_is_unknown_and_split_interior_is_not_one_room():
    assert assess_interior({(1, 1), (3, 1)}, [room()])['enclosed'] is None
    other = room(); other['id'] = 43; other['visible_cells'] = [{'x': 3, 'z': 1}]
    assert assess_interior({(1, 1), (3, 1)}, [room(), other])['enclosed'] is False


def test_native_room_verification_can_be_invalidated_by_removed_roof():
    native = room()
    assert assess_interior({(1, 1)}, [native])['roofed']
    native['open_roof_count'] = 1
    assert not assess_interior({(1, 1)}, [native])['roofed']
