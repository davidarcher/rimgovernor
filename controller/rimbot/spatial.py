"""Shared planned-space constraints; native observations still own actual access."""
from .colony_plan import Buildings, RoomShell, Zone, Placement


def room_entrance(room):
    b = room.bounds
    return {'north': ((b.x+b.width//2, b.z+b.height-1), (0, 1)),
            'south': ((b.x+b.width//2, b.z), (0, -1)),
            'east': ((b.x+b.width-1, b.z+b.height//2), (1, 0)),
            'west': ((b.x, b.z+b.height//2), (-1, 0))}[room.entrance]


def entrance_cells(room):
    (x, z), (dx, dz) = room_entrance(room)
    return {(x-dx, z-dz), (x, z), (x+dx, z+dz)}


def room_placements(room: RoomShell):
    b = room.bounds
    door, _ = room_entrance(room)
    perimeter = [(x, z) for x, z in b.cells()
                 if x in (b.x, b.x+b.width-1) or z in (b.z, b.z+b.height-1)]
    perimeter.sort(key=lambda c: c != door)
    return [Placement(x=x, z=z, def_name=room.door_def if (x, z)==door else room.wall_def,
        rotation=room.entrance if (x, z)==door else 'north', materials=room.materials) for x, z in perimeter]


class GeometryConflict(ValueError):
    def __init__(self, step, other, point):
        self.evidence = {'step_id': step, 'conflicts_with': other,
                         'cell': {'x': point[0], 'z': point[1]}}
        super().__init__(f'{step} overlaps {other} at x={point[0]}, z={point[1]}; '
                         'adjust the conflicting footprint or reserved walkway')


def native_footprint(preview, placement):
    """An exact-rotation preview must supply its full, nonempty native footprint."""
    rows = preview.get('rotations') or []
    if (not isinstance(rows, list) or len(rows) != 1 or not isinstance(rows[0], dict)
            or rows[0].get('rotation') != placement.rotation):
        raise ValueError('Native footprint needs one matching rotation')
    cells = rows[0].get('occupiedCells')
    if not isinstance(cells, list) or not cells:
        raise ValueError('Native building footprint is unavailable')
    if any(not isinstance(c, dict) or any(type(c.get(k)) is not int for k in ('x', 'z')) for c in cells):
        raise ValueError('Native building footprint has invalid coordinates')
    points = {(c['x'], c['z']) for c in cells}
    if len(points) != len(cells) or (placement.x, placement.z) not in points:
        raise ValueError('Native building footprint does not match its placement')
    return points


def projected_obstruction(preview, placement):
    """Use the completed native definition, including custom building classes."""
    footprint = native_footprint(preview, placement)
    passability = preview.get('passability')
    if passability not in {'Standable', 'PassThroughOnly', 'Impassable'}:
        raise ValueError('Native completed-building passability is unavailable')
    if type(preview.get('isDoor')) is not bool:
        raise ValueError('Native door classification is unavailable')
    # Doors remain a candidate connection; native pawn traversal still decides
    # whether a particular pawn can open the current door.
    return footprint if passability == 'Impassable' and not preview['isDoor'] else set()


def validate_geometry(spec, footprints=None, *, current=None):
    """Footprints keyed by (step ID, slot) refine the conservative anchor checks."""
    footprints = footprints or {}
    reserved = {c for r in spec.reserved_walkways for c in r.cells()}
    previous = {step.id: step for step in current.spec.steps} if current else {}
    def inactive(step):
        old = previous.get(step.id)
        progress = current.progress.get(step.id) if current else None
        return bool(old and progress and progress.state in ('complete','cancelled') and old.action == step.action)
    active = [step for step in spec.steps if not inactive(step)]
    def transferred(step, slot):
        handoff = current.control.get('wall_handoffs', {}).get(f'{step.id}:{slot}') if current else None
        old = previous.get(step.id)
        if not handoff or not old or old.action != step.action:
            return False
        from .wall_upgrade import removal_record
        try:
            removal, progress = removal_record(current, handoff['removal'])
        except (ValueError, KeyError):
            return False
        proposed = next((s for s in spec.steps if s.id == removal.id), None)
        return bool((proposed is None or proposed.signature() == removal.signature())
            and progress.state == 'complete'
            and removal.signature() == handoff.get('signature'))
    rooms = [(s.id, s.action) for s in active if isinstance(s.action, RoomShell)]
    claimed = {}
    for step in active:
        action = step.action
        placements = room_placements(action) if isinstance(action, RoomShell) else (
            action.placements if isinstance(action, Buildings) else [])
        groups = [(str(i), footprints.get((step.id, str(i)), {(p.x, p.z)}))
                  for i, p in enumerate(placements)
                  if not transferred(step, i)]
        if isinstance(action, Zone):
            groups = [('zone', {c for patch in action.patches for c in patch.cells()})]
        for slot, points in groups:
            for point in sorted(points):
                if point in reserved:
                    raise GeometryConflict(step.id, 'reserved walkway', point)
                if point in claimed and claimed[point] != (step.id, slot):
                    raise GeometryConflict(step.id, claimed[point][0], point)
                claimed[point] = (step.id, slot)
            for room_id, room in rooms:
                # Storage zones are traversable; furniture must leave the doorway
                # and its immediate inside/outside approaches free.
                if placements and step.id != room_id:
                    conflict = points & entrance_cells(room)
                    if conflict:
                        raise GeometryConflict(step.id, room_id+' entrance', min(conflict))
                if isinstance(action, Zone) and action.zone_type == 'growing':
                    conflict = points & set(room.bounds.cells())
                    if conflict:
                        raise GeometryConflict(step.id, room_id, min(conflict))
        if isinstance(action, RoomShell):
            for other_id, other in rooms:
                if other_id == step.id:
                    continue
                conflict = set(action.bounds.cells()) & set(other.bounds.cells())
                if conflict:
                    raise GeometryConflict(step.id, other_id, min(conflict))
