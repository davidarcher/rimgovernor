"""Bounded native shell checks; immediate access is not a pawn route guarantee."""
from .spatial import room_entrance


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
