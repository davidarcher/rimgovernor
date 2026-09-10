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
            return method, [dict(kind='create_zone', zone_type='stockpile', label='RimGovernor supplies '+method[-8:],
                patches=[dict(x=x, z=z, width=2, height=2)], preset='nothing', allow=definitions,
                priority='Important', covered_empty=True)]
        if attempts >= 8:
            break
    return None


async def supply_storeroom(rt, facts):
    """One small room, only after existing covered storage cannot be reused."""
    from .shelter_handoff import verified_room
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
    from .upkeep_sites import enclosure_site
    shell = await enclosure_site(rt, facts, goal)
    return 'supply-room-' + fingerprint(shell)[:16], [shell]
