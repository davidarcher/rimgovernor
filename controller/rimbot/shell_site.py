"""Bounded native shell checks; immediate access is not a pawn route guarantee."""
from .spatial import room_entrance
from .colony_plan import RoomShell


ZONE_ARGUMENTS = dict(includeCells=True, includeContents=False, maxCellsPerZone=10000)


class ShellSiteRefusal(ValueError):
    def __init__(self, step_id, code, detail, **evidence):
        self.code = code
        self.evidence = dict(step_id=step_id, **evidence)
        super().__init__(detail)


def cell_set(rows):
    if not isinstance(rows, list) or any(not isinstance(row, dict) or
            any(type(row.get(k)) is not int for k in ('x', 'z')) for row in rows):
        raise ValueError('Missing or invalid native cell coordinates')
    result = {(row['x'], row['z']) for row in rows}
    if len(result) != len(rows):
        raise ValueError('Duplicate native cell coordinates')
    return result


def validate_shell_zones(step_id, shell, census):
    """Use the unfiltered census: radius filtering follows zone lists, not the grid."""
    def unknown(detail):
        raise ShellSiteRefusal(step_id, 'incomplete_zone_geometry', detail)
    zones = census.get('zones')
    if (census.get('success') is not True or not isinstance(zones, list)
            or census.get('zoneCount') != len(zones) or census.get('zoneCountOnMap') != len(zones)
            or (census.get('totals') or {}).get('gridSweepFailed') is not False):
        unknown('Cannot verify the complete native zone census for this shell')
    bounds = set(shell.bounds.cells())
    perimeter = {(x, z) for x, z in bounds if x in (shell.bounds.x, shell.bounds.x+shell.bounds.width-1)
                 or z in (shell.bounds.z, shell.bounds.z+shell.bounds.height-1)}
    for zone in zones:
        if not isinstance(zone, dict):
            unknown('Invalid native zone record')
        try:
            listed, grid = cell_set(zone.get('cells')), cell_set(zone.get('gridCells'))
        except ValueError as error:
            unknown(str(error))
        if (zone.get('listedCellCount') != len(listed) or zone.get('gridCellCount') != len(grid)
                or zone.get('cellsNotListed') != 0 or zone.get('gridCellsNotListed') != 0):
            unknown('Native zone geometry was truncated or has unknown counts')
        overlap = bounds & (listed | grid)
        if not overlap:
            continue
        if zone.get('consistent') is not True or listed != grid:
            unknown('Zone list/grid disagreement intersects the proposed shell')
        growing = zone.get('type') == 'Zone_Growing' or 'plantDefExplicitlySet' in zone
        stockpile = zone.get('type') == 'Zone_Stockpile' or 'filterSummary' in zone
        conflict = overlap if growing or not stockpile else overlap & perimeter
        if conflict:
            point = min(conflict)
            raise ShellSiteRefusal(step_id, 'existing_zone',
                'Room shell encloses or crosses an existing native zone; refine the site or zone explicitly',
                zone_id=zone.get('id'), zone=zone.get('label'), cell=dict(x=point[0], z=point[1]))


async def validate_shell_access(step_id, shell, read):
    (x, z), (dx, dz) = room_entrance(shell)
    approaches = {(x-dx, z-dz), (x+dx, z+dz)}
    expected = approaches | {(x, z)}
    args = dict(x=min(c[0] for c in expected), z=min(c[1] for c in expected),
        width=3 if dx else 1, height=3 if dz else 1, fields='fogged,walkable,passable', sparse=False)
    result = await read('home/get_cells_plus', args)
    try:
        cells = cell_set(result.get('cells'))
    except ValueError as error:
        raise ShellSiteRefusal(step_id, 'incomplete_entrance_geometry', str(error)) from error
    if (result.get('success') is not True or cells != expected or result.get('cellCount') != 3
            or result.get('cellsOmitted') != 0
            or not {'fogged', 'walkable', 'passable'} <= set(result.get('fieldsApplied') or [])):
        raise ShellSiteRefusal(step_id, 'incomplete_entrance_geometry',
            'Native entrance observations are incomplete or do not match the requested cells')
    for cell in result['cells']:
        if (cell['x'], cell['z']) not in approaches:
            continue
        # The native tool omits fogged only when false and reports selected fields.
        if cell.get('fogged', False) is not False or cell.get('walkable') is not True or cell.get('passable') is not True:
            raise ShellSiteRefusal(step_id, 'entrance_unavailable',
                'Room entrance needs observed walkable inside and outside approaches',
                cell=cell)


async def validate_shell_site(step_id, shell, read):
    census = await read('home/list_zones', dict(ZONE_ARGUMENTS))
    validate_shell_zones(step_id, shell, census)
    await validate_shell_access(step_id, shell, read)


def connected_cells(start, allowed):
    reached = {start} if start in allowed else set()
    pending = list(reached)
    while pending:
        x, z = pending.pop()
        for neighbor in ((x-1, z), (x+1, z), (x, z-1), (x, z+1)):
            if neighbor in allowed and neighbor not in reached:
                reached.add(neighbor)
                pending.append(neighbor)
    return reached


async def validate_shell_connectivity(step_id, shell, read, spec):
    """Prove local grid connectivity against observed cells and projected shells.

    A three-cell exterior margin bounds reads; reaching its edge does not prove
    a route to any pawn or to the map edge. Native doors and pawn restrictions
    still own actual traversal. No inferred route is persisted as owned space.
    """
    b = shell.bounds
    x0, z0 = max(0, b.x-3), max(0, b.z-3)
    x1, z1 = b.x+b.width+2, b.z+b.height+2
    observed = {}
    # Native detailed cells are capped at 1024 per query. The first cell read
    # supplies the map bounds before any rectangle can cross the map edge.
    initial = await read('home/get_cells_plus', dict(x=b.x, z=b.z, width=1, height=1,
        fields='fogged,walkable,passable', sparse=False))
    size = initial.get('mapSize') or {}
    if initial.get('success') is not True or any(type(size.get(k)) is not int or size[k] <= 0 for k in ('x', 'z')):
        raise ShellSiteRefusal(step_id, 'incomplete_connectivity', 'Native map bounds are unavailable')
    map_size = (size['x'], size['z'])
    x1, z1 = min(x1, size['x']-1), min(z1, size['z']-1)
    if b.x+b.width > size['x'] or b.z+b.height > size['z']:
        raise ShellSiteRefusal(step_id, 'incomplete_connectivity', 'Room bounds exceed the observed map')
    for x in range(x0, x1+1, 32):
        for z in range(z0, z1+1, 32):
            width, height = min(32, x1-x+1), min(32, z1-z+1)
            result = await read('home/get_cells_plus', dict(x=x, z=z, width=width, height=height,
                fields='fogged,walkable,passable', sparse=False))
            expected = {(a, c) for a in range(x, x+width) for c in range(z, z+height)}
            try:
                points = cell_set(result.get('cells'))
            except ValueError as error:
                raise ShellSiteRefusal(step_id, 'incomplete_connectivity', str(error)) from error
            if (result.get('success') is not True or points != expected
                    or result.get('cellCount') != len(expected) or result.get('cellsOmitted') != 0
                    or not {'fogged', 'walkable', 'passable'} <= set(result.get('fieldsApplied') or [])
                    or tuple((result.get('mapSize') or {}).get(k) for k in ('x', 'z')) != map_size):
                raise ShellSiteRefusal(step_id, 'incomplete_connectivity', 'Native connectivity census is incomplete or changed maps')
            observed.update({(c['x'], c['z']): c for c in result['cells']})
    allowed = {p for p, c in observed.items() if c.get('fogged', False) is False
               and c.get('walkable') is True and c.get('passable') is True}
    interior = {(x, z) for x in range(b.x+1, b.x+b.width-1) for z in range(b.z+1, b.z+b.height-1)}
    # Unknown interior cells cannot be silently discarded as unusable rock.
    if any(observed[p].get('fogged', False) is not False or
           any(type(observed[p].get(k)) is not bool for k in ('walkable', 'passable')) for p in interior):
        raise ShellSiteRefusal(step_id, 'incomplete_connectivity', 'Room interior has fogged or unknown traversal evidence')
    (door_x, door_z), (dx, dz) = room_entrance(shell)
    inside, outside = (door_x-dx, door_z-dz), (door_x+dx, door_z+dz)
    reachable = connected_cells(inside, allowed & interior)
    disconnected = (allowed & interior) - reachable
    if inside not in reachable or disconnected:
        point = min(disconnected) if disconnected else inside
        raise ShellSiteRefusal(step_id, 'disconnected_interior',
            'Usable room cells do not connect to the planned entrance', cell=dict(x=point[0], z=point[1]))
    exterior = allowed - set(b.cells())
    for step in spec.steps:
        if step.id == step_id or not isinstance(step.action, RoomShell):
            continue
        other = step.action
        ob = other.bounds
        door, _ = room_entrance(other)
        exterior -= {(x, z) for x, z in ob.cells() if (x, z) != door
                     and (x in (ob.x, ob.x+ob.width-1) or z in (ob.z, ob.z+ob.height-1))}
    reachable = connected_cells(outside, exterior)
    boundary = {(x, z) for x, z in exterior if
                (x in (x0, x1) and 0 < x < size['x']-1) or (z in (z0, z1) and 0 < z < size['z']-1)}
    if not reachable & boundary:
        raise ShellSiteRefusal(step_id, 'disconnected_entrance',
            'Room entrance has no observed route to the edge of the local construction margin',
            cell=dict(x=outside[0], z=outside[1]))
    return dict(interior_cells=len(interior & allowed), reachable_exterior_cells=len(reachable),
                observation_cells=len(observed), margin=3)
