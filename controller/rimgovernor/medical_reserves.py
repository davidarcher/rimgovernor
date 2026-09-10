"""Observed medicine reserves using the shared native resource acquisition path."""
from .native_forecasts import finite


def reserve_evidence(facts, control):
    raw = facts.get('upkeep') or {}
    items, resources = raw.get('items'), facts.get('resources')
    if (raw.get('version') != 1 or raw.get('tick') != facts.get('tick') or 'items' in raw.get('errors', {})
            or not isinstance(items, list) or not isinstance(resources, dict)
            or type(facts.get('colonists')) is not int or facts['colonists'] < 0
            or any(type(r.get('medicine')) is not bool for r in items)):
        return None
    if facts['colonists'] == 0:
        return []
    medicine = [r for r in items if r['medicine']]
    if any(type(r.get('count')) is not int or r['count'] < 0 or type(r.get('forbidden')) is not bool
           or not isinstance(r.get('defName'), str) for r in medicine):
        return None
    types = {r['defName'] for r in medicine}
    if any(finite(resources.get(name, 0)) is None for name in types):
        return None
    # Resource facts already exclude forbidden and unreachable stock. Cap them
    # against observed usable stacks; future harvest and held stock earn no credit.
    stock = sum(min(resources.get(name, 0), sum(r['count'] for r in medicine if r['defName'] == name
        and not r['forbidden'] and (r.get('perishable') is False or finite(r.get('rotTicks')) is not None and r['rotTicks'] > 0)))
        for name in types)
    policy = control.get('policy', {})
    entry = facts['colonists'] * policy.get('medicine_min_per_colonist', 1)
    recovery = facts['colonists'] * policy.get('medicine_target_per_colonist', 3)
    active = stock < (recovery if control.get('medical_reserve_active') else entry)
    control['medical_reserve_active'] = active
    control['medical_reserve'] = dict(stock=stock, entry=entry, recovery=recovery)
    return [dict(id='medical-reserve', count=recovery-stock, stock=stock, target=recovery)] if active else []


async def reserve_method(rt, facts):
    from .colony_skills import SkillBlocked
    from .production_policy import resource_method
    state = rt.current_plan.control['upkeep']['MaintainMedicalReserves']
    rt.current_plan.colony_goals['MaintainMedicalReserves'].evidence.pop('waiting_for_medical_stock', None)
    if not state['known']:
        raise SkillBlocked('Current usable medicine reserve is unavailable')
    if not state['targets']:
        return None
    if 'MedicineHerbal' not in facts.get('policyResources', {}):
        raise SkillBlocked('Native herbal medicine resource definition is unavailable')
    row = state['targets'][0]
    goal = rt.current_plan.colony_goals['MaintainMedicalReserves']
    # Existing better medicine reduces the replenishment amount; no care policy,
    # drug restriction or existing production bill is replaced to meet this target.
    goal.target = dict(resource='MedicineHerbal', quantity=facts['resources'].get('MedicineHerbal', 0) + row['count'])
    result = await resource_method(rt, 'MaintainMedicalReserves', facts)
    if result is None:
        goal.evidence['waiting_for_medical_stock'] = True
    return result
