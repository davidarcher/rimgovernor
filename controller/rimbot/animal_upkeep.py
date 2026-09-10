"""Starting-animal containment through ordinary construction and native handling."""
from .colony_plan import RoomShell
from .strategic_state import fingerprint


def containment_evidence(facts):
    raw = facts.get('upkeep') or {}
    animals = raw.get('animals')
    if (raw.get('version') != 1 or raw.get('tick') != facts.get('tick')
            or 'animals' in raw.get('errors', {}) or not isinstance(animals, list)):
        return None
    if any(type(a.get('requiresPen')) is not bool or not isinstance(a.get('id'), str)
           or a['requiresPen'] and any(type(a.get(k)) is not bool for k in ('contained', 'release', 'slaughter')) for a in animals):
        return None
    return sorted((a for a in animals if a['requiresPen'] and not a['contained']
                   and not a.get('release') and not a.get('slaughter')), key=lambda a: a['id'])


async def containment_method(rt, facts, people):
    from .colony_skills import SkillBlocked
    goal = rt.current_plan.colony_goals['MaintainAnimalContainment']
    goal.evidence.pop('waiting_for_native_pen', None)
    state = rt.current_plan.control['upkeep']['MaintainAnimalContainment']
    if not state['known']:
        raise SkillBlocked('Native animal pen requirements or containment is unavailable')
    rows = state['targets']
    if not rows:
        return None
    if all(a.get('suitablePen') for a in rows):
        enabled = [p for p in people if not any(p.get(k) for k in ('dead', 'downed', 'drafted', 'mentalState'))
            and any(w.get('name') == 'Handling' and w.get('disabled') is False and w.get('priority', 0) > 0
                    for w in (p.get('work') or {}).get('types', []))]
        if not enabled:
            raise SkillBlocked('No enabled available handler; preserve player work overrides')
        goal.evidence['waiting_for_native_pen'] = True
        return None
    if len(rows) > 8:
        raise SkillBlocked('Starting herd exceeds bounded pen admission; explicit capacity planning required')
    prior = [s for s in rt.current_plan.spec.steps if s.goal_id == 'MaintainAnimalContainment'
             and isinstance(s.action, RoomShell)]
    if not prior:
        from .upkeep_sites import enclosure_site
        shell = await enclosure_site(rt, facts, goal, wall='Fence', door='FenceGate', empty_interior=False)
        return 'pen-shell-' + fingerprint(shell)[:16], [shell]
    shell = prior[0]
    if rt.current_plan.progress[shell.id].state != 'complete':
        raise SkillBlocked('Existing pen construction needs observation; no duplicate enclosure admitted')
    method = 'pen-marker-' + fingerprint(shell.action.model_dump())[:16]
    if goal.method_seen(method):
        raise SkillBlocked('Completed pen marker has no observed suitable enclosure; inspect native pen eligibility')
    from .development import placement
    bounds = shell.action.bounds
    interior = {(x, z) for x in range(bounds.x+1, bounds.x+bounds.width-1)
                for z in range(bounds.z+1, bounds.z+bounds.height-1)}
    local = dict(facts, cells=[c for c in facts.get('cells', []) if (c['x'], c['z']) in interior])
    action = await placement(rt, local, 'PenMarker', near=dict(x=bounds.x+1, z=bounds.z+1), radius=4, goal=goal)
    return method, [action]
