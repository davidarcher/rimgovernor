"""Bounded covered stockpiles for currently exposed supplies."""
from .colony_plan import Buildings, RoomShell, Zone
from .strategic_state import fingerprint


async def covered_storage(rt, facts, targets):
    """Reuse existing roofing; the native dispatch guard preserves changed cells."""
    cells = facts.get('cells')
    if not isinstance(cells, list):
        return None
    definitions = sorted({r['defName'] for r in targets if isinstance(r.get('defName'), str)})[:32]
    if not definitions:
        return None
    plan = rt.current_plan
    goal = plan.colony_goals['SecureSupplies']
    for step in plan.spec.steps:
        if step.goal_id == 'SecureSupplies' and isinstance(step.action, RoomShell):
            if plan.progress[step.id].state != 'complete':
                return None
            from .shelter_handoff import verified_room
            if await verified_room(rt, step.action.model_dump()) is None:
                goal.evidence['waiting_for_storage_roof'] = True
                return None
    # Capacity failure must not allocate an unbounded succession of stockpiles.
    if sum(s.goal_id == 'SecureSupplies' and isinstance(s.action, Zone) for s in plan.spec.steps) >= 3:
        return None
    protected = {c for rect in plan.spec.reserved_walkways for c in rect.cells()}
    for step in plan.spec.steps:
        if isinstance(step.action, RoomShell):
            from .shelter_handoff import entrance_aisle
            protected.update(entrance_aisle(step.action.model_dump(), set(step.action.bounds.cells())))
        if plan.progress[step.id].state in ('complete', 'cancelled'):
            continue
        action = step.action
        if isinstance(action, RoomShell):
            protected.update(action.bounds.cells())
        elif isinstance(action, Zone):
            protected.update(c for rect in action.patches for c in rect.cells())
        elif isinstance(action, Buildings):
            for p in action.placements:
                definition = facts.get('definitions', {}).get(p.def_name, {})
                span = max(definition.get('width', 1), definition.get('height', 1))
                protected.update((x, z) for x in range(p.x-span, p.x+span+1)
                                 for z in range(p.z-span, p.z+span+1))
    available = {(c['x'], c['z']) for c in cells if c.get('roofed') is True
                 and c.get('walkable') is True and c.get('zone') is False
                 and c.get('occupied') is False and c.get('storageEmpty') is True} - protected
    center = facts.get('center', targets[0])
    candidates = sorted(available, key=lambda c: ((c[0]-center.get('x', 0))**2
        + (c[1]-center.get('z', 0))**2, c))
    attempts = 0
    for x, z in candidates:
        patch = {(x+dx, z+dz) for dx in range(2) for dz in range(2)}
        if not patch <= available:
            continue
        method = 'covered-storage-' + fingerprint(sorted(patch))[:16]
        if goal.method_seen(method):
            continue
        preview = await rt.inspect_native('home/zone_cells', dict(op='create', zoneType='stockpile',
            x=x, z=z, width=2, height=2, preset='nothing', allow=','.join(definitions),
            priority='Important', requireCoveredEmpty=True, dryRun=True, watch=False))
        attempts += 1
        if preview.get('success') is True and preview.get('cellsAccepted') == 4:
            goal.evidence['storage_preview'] = preview
            return method, [dict(kind='create_zone', zone_type='stockpile', label='RimBot supplies '+method[-8:],
                patches=[dict(x=x, z=z, width=2, height=2)], preset='nothing', allow=definitions,
                priority='Important', covered_empty=True)]
        if attempts >= 8:
            break
    return None


async def supply_storeroom(rt, facts):
    """One small room, only after existing covered storage cannot be reused."""
    from .capacity_growth import protected_cells
    from .hands import room_placements
    from .shelter_handoff import safe_rotation, verified_room
    from .spatial import room_entrance
    from .colony_skills import SkillBlocked
    plan = rt.current_plan
    goal = plan.colony_goals['SecureSupplies']
    if goal.evidence.get('waiting_for_storage_roof'):
        return None
    if sum(s.goal_id == 'SecureSupplies' and isinstance(s.action, Zone) for s in plan.spec.steps) >= 3:
        raise SkillBlocked('Supply storage capacity limit reached; inspect existing filters and deliveries')
    prior = [s for s in plan.spec.steps if s.goal_id == 'SecureSupplies' and isinstance(s.action, RoomShell)]
    if prior:
        step = prior[0]
        if plan.progress[step.id].state == 'complete':
            if await verified_room(rt, step.action.model_dump()) is None:
                goal.evidence['waiting_for_storage_roof'] = True
                return None
        raise SkillBlocked('Existing supply storeroom needs observation or free capacity; no duplicate room admitted')
    definitions = facts.get('definitions', {})
    if any(definitions.get(name, {}).get('available') is not True for name in ('Wall', 'Door')):
        raise SkillBlocked('Native wall and door definitions unavailable for supply storage')
    material = definitions['Wall'].get('stuff')
    if not material or material != definitions['Door'].get('stuff'):
        raise SkillBlocked('No shared observed wall and door material for supply storage')
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
        if not all(cells[(a, b)].get('storageEmpty') is True for a in range(x+1, x+5) for b in range(z+1, z+5)):
            continue
        shell = RoomShell(bounds=dict(x=x, z=z, width=6, height=6), wall_def='Wall', door_def='Door',
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
                blockedCells=';'.join(f'{p.x},{p.z}' for p in placements if p.def_name == 'Wall'),
                targetCells=f'{dx},{dz};{x+1},{z+1}'))
            if access.get('success') is True and any(p.get('targets') and all(
                    t.get('nativeReachable') is True and t.get('projectedReachable') is True for t in p['targets'])
                    for p in access.get('pawns', [])):
                goal.evidence['storeroom_site'] = dict(previews=previews, access=access)
                return 'supply-room-' + fingerprint(shell.model_dump())[:16], [shell.model_dump()]
        if tried >= 3:
            break
    raise SkillBlocked('No safe accessible supply storeroom in bounded native search')
