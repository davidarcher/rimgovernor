"""Maintain reachable animal reserves with native diets and shared acquisition."""
from math import ceil

from .native_forecasts import finite, forecasts


def feed_evidence(facts, control, goals=None):
    raw = facts.get('upkeep') or {}
    animals = raw.get('animals')
    if (raw.get('version') != 1 or raw.get('tick') != facts.get('tick')
            or 'animals' in raw.get('errors', {}) or not isinstance(animals, list)
            or any(not isinstance(a.get('id'), str) or any(type(a.get(k)) is not bool
                for k in ('release', 'slaughter')) for a in animals)):
        return None
    directed = {g.target.get('race') for key, g in (goals or {}).items()
                if key.startswith('MaintainHerd-') and g.source == 'PLAYER'}
    animals = [a for a in animals if not a['release'] and not a['slaughter'] and a.get('defName') not in directed]
    if not animals:
        control['animal_feed_active'] = {}
        return []
    feed = forecasts(facts)['animalFeed']
    inputs = facts.get('nativeForecastInputs') or {}
    if inputs.get('tick') != facts['tick'] or not feed['readable']:
        return None
    consumers = {r['id']: r for r in feed['consumers']}
    if any(a['id'] not in consumers or finite(consumers[a['id']].get('runwayDays')) is None for a in animals):
        return None
    policy = control.get('policy', {})
    entry, recovery = policy.get('animal_feed_min_days', 2), policy.get('animal_feed_target_days', 4)
    latches = control.setdefault('animal_feed_active', {})
    active, rows = {}, []
    for animal in animals:
        row = consumers[animal['id']]
        active[row['id']] = row['runwayDays'] < (recovery if latches.get(row['id']) else entry)
        if active[row['id']]:
            rows.append(dict(row, count=max(0, recovery * row['nutritionPerDay'] - row['usableNutrition']),
                             targetDays=recovery))
    control['animal_feed_active'] = active
    return sorted(rows, key=lambda r: (r['runwayDays'], r['id']))


async def feed_method(rt, facts):
    from .colony_skills import SkillBlocked
    from .production_policy import resource_method
    plan = rt.current_plan
    goal = plan.colony_goals['MaintainAnimalFeed']
    goal.evidence.pop('waiting_for_animal_feed', None)
    state = plan.control['upkeep']['MaintainAnimalFeed']
    if not state['known']:
        raise SkillBlocked('Current native animal demand, diet or reachable feed is unavailable')
    if not state['targets']:
        return None
    raw = facts['upkeep']
    candidates = raw.get('feedDefinitions')
    if 'feedDefinitions' in raw.get('errors', {}) or not isinstance(candidates, list):
        raise SkillBlocked('Native dedicated feed definitions are unavailable')
    demand = {r['id']: r['nutritionPerDay'] for r in facts['nativeForecastInputs']['combinedFoodSupply']['consumers']}
    worst = state['targets'][0]
    selected = goal.evidence.get('feed_resource')
    if selected and not any(r.get('defName') == selected and worst['id'] in r.get('eaters', []) for r in candidates):
        goal.evidence.pop('feed_resource', None)
        selected = None
    candidates = [r for r in candidates if worst['id'] in r.get('eaters', [])
        and finite(r.get('nutritionPerItem')) is not None and r['nutritionPerItem'] > 0
        and isinstance(r.get('defName'), str) and r['defName'] in facts.get('policyResources', {})
        and (not selected or r['defName'] == selected)]
    failures = []
    for feed in sorted(candidates, key=lambda r: (-len(r['eaters']), r['defName']))[:8]:
        if any(eater not in demand or finite(demand[eater]) is None for eater in feed['eaters']):
            continue
        resource = feed['defName']
        if plan.control.get('resource_policy', {}).get(resource, {}).get('spending', 'normal') != 'normal':
            failures.append(resource + ': restricted by player resource policy')
            continue
        quantity = ceil(sum(demand[eater] for eater in set(feed['eaters'])) * worst['targetDays'] / feed['nutritionPerItem'])
        if not 0 < quantity <= 100000:
            continue
        goal.target = dict(resource=resource, quantity=quantity)
        goal.evidence['feed_capacity'] = dict(resource=resource, required_stock=quantity,
            potential_eaters=feed['eaters'], nutrition_per_item=feed['nutritionPerItem'],
            actual_completion='Observed reachable feed runway, including rot and competing eaters')
        if facts.get('resources', {}).get(resource, 0) >= quantity:
            raise SkillBlocked('Feed stock exists but animal access or spoilage prevents reserve recovery; staging required')
        try:
            result = await resource_method(rt, 'MaintainAnimalFeed', facts)
        except SkillBlocked as error:
            failures.append(resource + ': ' + str(error))
            continue
        goal.evidence['feed_resource'] = resource
        if result is None:
            goal.evidence['waiting_for_animal_feed'] = True
        return result
    raise SkillBlocked('No bounded native feed acquisition is available: ' + '; '.join(failures))
