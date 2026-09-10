"""Preserve sleeping capacity while upgrading controller-built floor spots."""
from .native_forecasts import finite
from .colony_plan import Buildings, RoomShell, Zone
from .shelter_handoff import safe_rotation
from .strategic_state import fingerprint


def sleeping_evidence(facts, control):
    raw = facts.get('upkeep') or {}
    if raw.get('version') != 1 or raw.get('tick') != facts.get('tick'):
        return None
    if any(k in raw.get('errors', {}) or not isinstance(raw.get(k), list) for k in ('beds', 'people')):
        return None
    people, beds = raw['people'], raw['beds']
    if not people:
        return [] if not facts.get('colonists') else None
    if any(not isinstance(p.get('id'), str) or 'ownedBed' not in p
            or finite(p.get('comfortableMin')) is None or finite(p.get('comfortableMax')) is None for p in people):
        return None
    if any(any(not isinstance(b.get(k), list) for k in ('owners', 'users', 'accessibleTo'))
            or any(type(b.get(k)) is not bool for k in ('humanlike', 'medical', 'prisoners', 'roofed'))
            or finite(b.get('restEffectiveness')) is None or finite(b.get('temperature')) is None for b in beds):
        return None
    used = control.setdefault('sleeping_use', {})
    context = facts.get('upkeep_context')
    deficits = []
    for p in people:
        def safe(b):
            return (b['humanlike'] and not b['medical'] and not b['prisoners'] and b['roofed']
                and p['id'] in b['accessibleTo'] and p['comfortableMin'] <= b['temperature'] <= p['comfortableMax'])
        owned = next((b for b in beds if b['id'] == p['ownedBed']), None)
        suitable = [b for b in beds if safe(b) and b['defName'] != 'SleepingSpot' and b['restEffectiveness'] > 0]
        if owned in suitable and p['id'] in owned['owners']:
            if p['id'] in owned['users']:
                used[p['id']] = dict(bed=owned['id'], context=context, tick=facts['tick'])
            previous = used.get(p['id'], {})
            if previous.get('bed') == owned['id'] and previous.get('context') == context:
                continue
            kind = 'use'
        elif owned is not None and not safe(owned):
            kind = 'unsafe'
        else:
            kind = 'upgrade'
        deficits.append(dict(id=p['id'], kind=kind, owned=owned, previousBed=p['ownedBed'],
            available=[b for b in suitable if not b['owners']], person=p))
    return sorted(deficits, key=lambda r: r['id'])


async def sleeping_method(rt, facts):
    from .colony_skills import SkillBlocked, native
    state = rt.current_plan.control['upkeep']['MaintainSleeping']
    goal = rt.current_plan.colony_goals['MaintainSleeping']
    goal.evidence.pop('waiting_for_native_sleep', None)
    refusals = []
    goal.evidence['sleeping_blockers'] = refusals
    if not state['known']:
        raise SkillBlocked('Sleeping eligibility, ownership or native bed effectiveness is unavailable')
    rows = state['targets']
    if not rows:
        return None
    targets = [r for r in rows if r['kind'] != 'use']
    if not targets:
        goal.evidence['waiting_for_native_sleep'] = True
        return None
    for row in targets:
        if row['kind'] == 'unsafe':
            refusals.append(dict(pawn=row['id'], reason='Existing sleeping place has unsafe access or temperature'))
            continue
        old = row['owned']
        if row['previousBed'] is not None:
            managed = old is not None and old['defName'] == 'SleepingSpot' and any(
                s.source == 'AUTOPILOT' and rt.current_plan.progress[s.id].state == 'complete'
                and isinstance(s.action, Buildings) and any(p.def_name == 'SleepingSpot'
                    and (p.x, p.z) == (old.get('x'), old.get('z'))
                    and rt.current_plan.progress[s.id].issued.get(str(i), {}).get('confirmed') is True
                    and rt.current_plan.progress[s.id].issued[str(i)].get('outcome') == 'placed'
                    and rt.current_plan.progress[s.id].issued[str(i)].get('placed_thing_id') == old['id']
                    for i, p in enumerate(s.action.placements))
                for s in rt.current_plan.spec.steps)
            if not managed:
                refusals.append(dict(pawn=row['id'], reason='Existing assignment lacks exact controller placement ownership'))
                continue  # A player bed assignment is not an upgrade authorization.
        for bed in sorted(row['available'], key=lambda b: b['id'])[:8]:
            args = dict(pawn=row['id'], bed=bed['id'], previousBed=row['previousBed'] or 'none')
            method = 'assign-bed-' + fingerprint(args)[:16]
            if goal.method_seen(method):
                continue
            preview = await rt.inspect_native('home/upkeep_bed', dict(args, dryRun=True))
            if preview.get('success') is True:
                return method, [native('home/upkeep_bed', **args)]
        method = 'build-bed-' + fingerprint({'pawn': row['id'], 'previous': row['previousBed']})[:16]
        if goal.method_seen(method):
            continue
        definition = facts.get('definitions', {}).get('Bed', {})
        if definition.get('available') is not True:
            refusals.append(dict(pawn=row['id'], reason='Native Bed definition or required research is unavailable'))
            continue
        protected = {c for rect in rt.current_plan.spec.reserved_walkways for c in rect.cells()}
        for step in rt.current_plan.spec.steps:
            if rt.current_plan.progress[step.id].state in ('complete', 'cancelled'):
                continue
            if isinstance(step.action, RoomShell): protected.update(step.action.bounds.cells())
            if isinstance(step.action, Zone): protected.update(c for rect in step.action.patches for c in rect.cells())
            if isinstance(step.action, Buildings):
                protected.update((x, z) for p in step.action.placements
                    for x in range(p.x-3, p.x+4) for z in range(p.z-3, p.z+4))
        cells = {(c['x'], c['z']) for c in facts.get('cells', []) if c.get('roofed') is True
            and c.get('walkable') is True and c.get('storageEmpty') is True and c.get('zone') is False
            and finite(c.get('temperature')) is not None
            and row['person']['comfortableMin'] <= c['temperature'] <= row['person']['comfortableMax']} - protected
        anchor = old or facts.get('center', {})
        candidates = sorted(cells, key=lambda c: ((c[0]-anchor.get('x', 0))**2 + (c[1]-anchor.get('z', 0))**2, c))
        for x, z in candidates[:24]:
            preview = await rt.inspect_native('home/place_building', dict(defName='Bed', x=x, z=z,
                rotation='north', stuff=definition.get('stuff'), dryRun=True))
            rotations = [r for r in preview.get('rotations', []) if safe_rotation(r)]
            if preview.get('canPlace') is not True or len(rotations) != 1:
                continue
            footprint = {(c['x'], c['z']) for c in rotations[0].get('occupiedCells', [])}
            if footprint and footprint <= cells:
                access = await rt.inspect_native('home/spatial_access', dict(blockedCells='',
                    targetCells=';'.join(f'{a},{b}' for a, b in sorted(footprint))))
                pawn = next((p for p in access.get('pawns', []) if p.get('pawn') == row['id'].removeprefix('Thing_')), {})
                if (access.get('success') is not True or not pawn.get('targets')
                        or any(t.get('nativeReachable') is not True or t.get('projectedReachable') is not True
                               for t in pawn['targets'])):
                    continue
                goal.evidence['bed_site'] = dict(preview=preview, access=access, pawn=row['id'])
                return method, [dict(kind='place_buildings', placements=[dict(def_name='Bed', x=x, z=z,
                    materials=[definition['stuff']] if definition.get('stuff') else [])])]
        refusals.append(dict(pawn=row['id'], reason='No safe bed footprint with native access in the bounded candidate set'))
    raise SkillBlocked('No safe bed upgrade preserving current assignments and sleeping capacity: '
                       + '; '.join(sorted({r['reason'] for r in refusals})) )
