"""Verify planned interiors against observed native room topology."""


def assess_interior(interior, rooms):
    if not interior or rooms is None:
        return {'enclosed': None, 'reachable': None, 'roofed': None}
    observed = set()
    for room in rooms:
        visible = {(p['x'], p['z']) for p in (room.get('visible_cells') or [])}
        observed.update(visible)
        if not interior <= visible:
            continue
        return {'room_id': room['id'],
                'enclosed': not room['touches_map_edge'] and not room['is_doorway'],
                'reachable': None if room.get('pawns_reaching_visible_cell') is None else bool(room['pawns_reaching_visible_cell']),
                'roofed': room['open_roof_count'] == 0,
                'scope': 'Native room containing the planned interior; reachability is to its visible anchor, not every furniture position.'}
    # A clipped map survey cannot prove that a missing room does not exist.
    return {'enclosed': False if interior <= observed else None,
            'reachable': None, 'roofed': None}
