"""Bounded native enclosure placement shared by supply rooms and animal pens."""
from .capacity_growth import protected_cells
from .colony_plan import RoomShell
from .hands import room_placements
from .shelter_handoff import safe_rotation
from .spatial import room_entrance
from .colony_skills import SkillBlocked


async def enclosure_site(rt, facts, goal, *, wall='Wall', door='Door', empty_interior=True):
    plan = rt.current_plan
    definitions = facts.get('definitions', {})
    if any(definitions.get(name, {}).get('available') is not True for name in (wall, door)):
        raise SkillBlocked('Native wall and door definitions unavailable for enclosure construction')
    material = definitions[wall].get('stuff')
    if not material or material != definitions[door].get('stuff'):
        raise SkillBlocked('No shared observed wall and door material for enclosure construction')
    protected = protected_cells(plan)
    cells = {(c['x'], c['z']): c for c in facts.get('cells', [])}
    free = {p for p, c in cells.items() if c.get('walkable') is True and c.get('occupied') is False
            and c.get('zone') is False and c.get('supportsLight') is True} - protected
    center = facts.get('center', {})
    ordered = sorted(free, key=lambda p: ((p[0]-center.get('x', 0))**2 + (p[1]-center.get('z', 0))**2, p))
    tried = 0
    for x, z in ordered:
        footprint = {(a, b) for a in range(x, x+6) for b in range(z, z+6)}
        if not footprint <= free:
            continue
        # The storage operation preserves plants and items. Do not spend materials
        # on an interior that cannot accept its guarded stockpile afterward.
        if empty_interior and not all(cells[(a, b)].get('storageEmpty') is True for a in range(x+1, x+5) for b in range(z+1, z+5)):
            continue
        shell = RoomShell(bounds=dict(x=x, z=z, width=6, height=6), wall_def=wall, door_def=door,
                          materials=[material], entrance='south', kind='build_room_shell')
        (dx, dz), (vx, vz) = room_entrance(shell)
        approach = (dx+vx, dz+vz)
        if approach not in free:
            continue
        tried += 1
        previews = []
        placements = room_placements(shell)
        for p in placements:
            preview = await rt.inspect_native('home/place_building', dict(defName=p.def_name, x=p.x, z=p.z,
                rotation=p.rotation, stuff=material, dryRun=True))
            previews.append(preview)
            if preview.get('canPlace') is not True or not any(safe_rotation(r) for r in preview.get('rotations', [])):
                break
        else:
            access = await rt.inspect_native('home/spatial_access', dict(
                blockedCells=';'.join(f'{p.x},{p.z}' for p in placements if p.def_name == wall),
                targetCells=f'{dx},{dz};{x+1},{z+1}'))
            if access.get('success') is True and any(p.get('targets') and all(
                    t.get('nativeReachable') is True and t.get('projectedReachable') is True for t in p['targets'])
                    for p in access.get('pawns', [])):
                goal.evidence['enclosure_site'] = dict(previews=previews, access=access)
                return shell.model_dump()
        if tried >= 3:
            break
    raise SkillBlocked('No safe accessible enclosure in bounded native search')
