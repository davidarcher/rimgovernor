"""Dining and recreation through usable native furniture and observed use."""
from .strategic_state import fingerprint


def comfort_evidence(facts, control):
    raw = facts.get('upkeep') or {}
    data = raw.get('comfort')
    if (raw.get('version') != 1 or raw.get('tick') != facts.get('tick') or 'comfort' in raw.get('errors', {})
            or not isinstance(data, dict) or any(not isinstance(data.get(k), list)
                for k in ('people', 'surfaces', 'dining', 'recreation'))):
        return None
    used = control.setdefault('comfort_use', {})
    context = facts.get('upkeep_context')
    people = set(data['people'])
    rows = []
    for kind in ('dining', 'recreation'):
        facilities = data[kind]
        if any(not isinstance(b.get('id'), str) or not isinstance(b.get('accessibleTo'), list)
               or not isinstance(b.get('users'), list) for b in facilities):
            return None
        available = [b for b in facilities if people & set(b['accessibleTo'])]
        missing = people - {p for b in available for p in b['accessibleTo']}
        for b in available:
            if people & set(b['users']) & set(b['accessibleTo']):
                used[kind] = dict(facility=b['id'], context=context, tick=facts['tick'])
        proof = used.get(kind, {})
        seen = proof.get('context') == context and any(b['id'] == proof.get('facility') for b in available)
        if people and (missing or not seen):
            rows.append(dict(id=kind, kind='capacity' if missing else 'use', missing=sorted(missing),
                             available=available))
    return rows


async def comfort_method(rt, facts):
    from .colony_skills import SkillBlocked
    from .development import placement
    goal = rt.current_plan.colony_goals['EnsureComfort']
    goal.evidence.pop('waiting_for_native_comfort', None)
    rows = facts.get('comfortUpkeep')
    if rows is None:
        raise SkillBlocked('Native dining and recreation capacity or use is unavailable')
    if not rows:
        return None
    missing = [r for r in rows if r['kind'] == 'capacity']
    if not missing:
        goal.evidence['waiting_for_native_comfort'] = True
        return None
    data = facts['upkeep']['comfort']
    kind = missing[0]['id']
    if data[kind]:
        raise SkillBlocked('Existing ' + kind + ' furniture lacks safe access for every colonist; preserve player areas')
    local = facts
    definition = 'HorseshoesPin'
    if kind == 'dining':
        if not data['surfaces']:
            definition = 'Table1x2c'
        else:
            definition = 'DiningChair'
            adjacent = {(c['x'], c['z']) for surface in data['surfaces'] for c in surface['adjacent']}
            local = dict(facts, cells=[c for c in facts.get('cells', []) if (c['x'], c['z']) in adjacent])
    prior = [s for s in rt.current_plan.spec.steps if s.goal_id == 'EnsureComfort'
        and s.action.kind == 'place_buildings' and any(p.def_name == definition for p in s.action.placements)]
    if prior:
        raise SkillBlocked('Previously admitted ' + definition + ' needs observation or access recovery; no duplicate furniture')
    action = await placement(rt, local, definition, indoors=True if kind == 'dining' else None, goal=goal)
    return 'comfort-' + fingerprint(action)[:12], [action]
