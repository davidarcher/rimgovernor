"""Maintained herd targets; native observations own eligibility and completion."""
from math import ceil, isfinite

from .native_forecasts import forecasts


def assess_herd(target, observed, feed):
    """Do not credit pregnancies, grass or pending orders as animals or stored feed."""
    result = {'readable': False, 'blockers': [], 'training': [], 'surplus': [],
              'population': None, 'feed_days': None}
    if observed.get('success') is not True or not isinstance(observed.get('animals'), list):
        result['blockers'].append('Native herd census unavailable')
        return result
    animals = [a for a in observed['animals'] if a['race'] == target['race']]
    result.update(readable=True, population=len(animals), animals=animals)
    consumers = {c['id']: c for c in feed.get('consumers') or []}
    days = [consumers.get(a['id'], {}).get('runwayDays') for a in animals]
    known_feed = feed.get('readable') is True and all(
        type(d) in (int, float) and isfinite(d) and d >= 0 for d in days)
    result['feed_days'] = min(days) if days and known_feed else None
    if animals and not known_feed:
        result['blockers'].append('Reachable stored feed unavailable')
    elif animals and result['feed_days'] < target['feed_days']:
        result['blockers'].append('Stored feed below seasonal reserve target')
    if any(a.get('contained') is not True for a in animals):
        result['blockers'].append('Native containment unavailable or animal outside suitable enclosure')
    result['waiting_births'] = len(animals) < target['minimum']
    protected = set(target['protected_ids'])
    eligible = [a for a in animals if a.get('safeToSlaughter') is True
                and a['id'] not in protected and not a.get('slaughter')]
    breeders = {gender: sum(a.get('gender') == gender and a.get('fertileAdult') is True
                           and not a.get('slaughter') and not a.get('release') for a in animals)
                for gender in ('Male', 'Female')}
    reserve = target.get('breeding_pairs', 0)
    if reserve and any(count < reserve for count in breeders.values()):
        result['blockers'].append('Fertile adult breeding reserve is below target; player separation and sterilization remain unchanged')
    result['breeders'] = breeders
    if result['waiting_births'] and not (any(a.get('pregnant') is True for a in animals)
            or all(count > 0 for count in breeders.values())):
        result['blockers'].append('Population below target; no observed pregnancy or fertile adult pair')
    pending = sum(a.get('slaughter') is True or a.get('release') is True for a in animals)
    surplus = max(0, len(animals) - target['maximum'] - pending)
    if surplus:
        if target['allow_slaughter']:
            remaining = dict(breeders)
            for animal in sorted(eligible, key=lambda a: a['id']):
                if len(result['surplus']) == surplus: break
                gender = animal.get('gender')
                if animal.get('fertileAdult') is True and gender in remaining:
                    if remaining[gender] <= reserve: continue
                    remaining[gender] -= 1
                result['surplus'].append(animal)
            if len(result['surplus']) < surplus:
                result['blockers'].append('Surplus animals are protected or native eligibility is unknown')
        else:
            result['blockers'].append('Population above target; slaughter has no player authorization')
    for animal in animals:
        if animal.get('slaughter') or animal.get('release') or animal['id'] in protected:
            continue
        rows = {r['name']: r for r in animal.get('training', [])}
        for name in target['trainables']:
            row = rows.get(name, {})
            if row.get('learned') is True:
                continue
            if row.get('canTrain') is not True:
                result['blockers'].append(f'{animal["id"]}: training {name} unavailable')
            else:
                result['training'].append({'animal': animal, 'name': name, 'wanted': row.get('wanted')})
    result['products_ready'] = [a['id'] for a in animals
                                if a.get('milkFull') is True or a.get('woolFull') is True]
    needs_handler = set(result['products_ready']) | {r['animal']['id'] for r in result['training']}
    result['handler_workload'] = {'training_targets': len(result['training']),
                                'ready_products': len(result['products_ready'])}
    for animal in animals:
        if animal['id'] in needs_handler and not any(h.get('priority', 0) > 0 for h in animal.get('handlers', [])):
            result['blockers'].append(f'{animal["id"]}: no active capable reachable handler')
    result['satisfied'] = (not result['blockers'] and not result['training']
                           and not result['surplus'] and not pending and not result['products_ready']
                           and not result['waiting_births'])
    return result


def validate_dispatch(plan, step_id, arguments):
    step = next((s for s in plan.spec.steps if s.id == step_id), None)
    goal = plan.colony_goals.get(step.goal_id) if step else None
    if (not step or not goal or goal.cancelled or goal.source != 'PLAYER'
            or not step.goal_id.startswith('MaintainHerd-') or arguments != step.action.arguments):
        raise ValueError('Husbandry requires an unchanged step owned by a player herd target')
    if arguments.get('slaughter') and (not goal.target.get('allow_slaughter')
            or arguments['animal'] in goal.target.get('protected_ids', [])):
        raise ValueError('Husbandry slaughter has no current player authorization')
    if arguments.get('trainable') and arguments['trainable'] not in goal.target.get('trainables', []):
        raise ValueError('Husbandry training has no current player authorization')
    return goal


def required_handler_skill(plan):
    levels = [a.get('minimumHandlingSkill') for key, goal in plan.colony_goals.items()
              if key.startswith('MaintainHerd-') and not goal.cancelled
              for a in goal.evidence.get('husbandry', {}).get('animals', [])]
    return {'Handling': max(levels)} if levels and all(type(n) is int for n in levels) else {}


def cancel_feed_work(plan, owner):
    for child in plan.colony_goals.values():
        if child.evidence.get('herd_owner') != owner: continue
        child.cancelled, child.status, child.reason = True, 'blocked', 'Cancelled with herd target'
        for step in child.steps:
            if step in plan.progress and plan.progress[step].state != 'complete': plan.cancel(step)


async def refresh_husbandry(rt, native):
    for key, child in rt.current_plan.colony_goals.items():
        owner = child.evidence.get('herd_owner')
        parent = rt.current_plan.colony_goals.get(owner) if owner else None
        if owner and (parent is None or parent.cancelled or parent.evidence.get('feed_goal') not in (None, key)):
            child.cancelled = True
            for step in child.steps:
                if step in rt.current_plan.progress and rt.current_plan.progress[step].state != 'complete':
                    rt.current_plan.cancel(step)
    goals = [(identity, goal) for identity, goal in rt.current_plan.colony_goals.items()
             if identity.startswith('MaintainHerd-') and not goal.cancelled]
    if not goals:
        return []
    plan, token, direction = rt.current_plan, rt.context_token, rt.chat_revision
    revision = plan.revision
    observed = await rt.game.invoke('home/husbandry_facts', {})
    await rt.ensure_context(token)
    if rt.current_plan is not plan or plan.revision != revision or rt.chat_revision != direction:
        raise InterruptedError('Herd observation was superseded by player direction or a changed plan')
    feed = forecasts(native)['animalFeed']
    nodes = []
    for identity, goal in goals:
        scope = {k: observed.get(k) for k in ('colonyId', 'mapId')}
        if goal.evidence.get('scope') != scope:
            cancel_feed_work(rt.current_plan, identity)
            goal.evidence['husbandry'] = {'readable': False, 'blockers': ['Herd belongs to another colony/map']}
            nodes.append((identity, 3))
            continue
        assessment = assess_herd(goal.target, observed, feed)
        goal.evidence['husbandry'] = assessment
        update_feed_goal(rt.current_plan, identity, goal, native, feed)
        # A player edit is never automatically reclaimed by a maintained goal.
        baseline = goal.evidence.setdefault('settings', {})
        for animal in assessment.get('animals', []):
            baseline.setdefault(animal['id'], animal['settingsToken'])
        if not assessment.get('satisfied'):
            nodes.append((identity, 3))
        if goal.status == 'blocked' and goal.reason.startswith('Husbandry:'):
            goal.status, goal.reason = 'active', ''
    return nodes


def update_feed_goal(plan, identity, goal, native, feed):
    """Reuse resource acquisition; stock targets never certify animal access or consumption."""
    from .colony_plan import ColonyGoal
    assessment = goal.evidence['husbandry']
    animals = {a['id'] for a in assessment.get('animals', [])}
    if not animals or feed.get('readable') is not True:
        return
    all_animals = set((native.get('nativeForecastInputs') or {}).get('animalIds') or [])
    stocks = ((native.get('nativeForecastInputs') or {}).get('combinedFoodSupply') or {}).get('stocks') or []
    resource = goal.target.get('feed_resource')
    choices = [s for s in stocks if animals <= set(s.get('eaters', [])) and s.get('holder') is None
               and type(s.get('count')) is int and s['count'] > 0
               and type(s.get('nutrition')) in (float, int) and isfinite(s['nutrition']) and s['nutrition'] > 0
               and (s.get('defName') == resource if resource else set(s.get('eaters', [])) <= all_animals)]
    if not choices:
        assessment['blockers'].append('No observed feed stock safely edible by the whole herd; choose or provide suitable feed')
        assessment['satisfied'] = False
        return
    selected = sorted(choices, key=lambda s: (s['defName'], s['id']))[0]
    resource = selected['defName']
    # Include competing consumers permitted to use this food. Demand is native,
    # not a species table; future births and temperature changes remain uncertain.
    consumers = ((native.get('nativeForecastInputs') or {}).get('combinedFoodSupply') or {}).get('consumers') or []
    by_id = {row['id']: row for row in consumers}
    eaters = set(selected['eaters'])
    demand = [by_id[a].get('nutritionPerDay') for a in eaters if a in by_id]
    if len(demand) != len(eaters) or any(type(d) not in (int, float) or not isfinite(d) or d <= 0 for d in demand):
        return
    if resource in plan.control.get('resource_policy', {}) and plan.control['resource_policy'][resource].get('spending', 'normal') != 'normal':
        assessment['blockers'].append('Selected feed resource is restricted by player policy')
        assessment['satisfied'] = False
        return
    count = ceil(sum(demand) * goal.target['feed_days'] / (selected['nutrition'] / selected['count']))
    key = 'MaintainResource-herd-' + goal.target['race'] + '-' + resource
    target = plan.colony_goals.setdefault(key, ColonyGoal(source='AUTOPILOT', priority_class=3))
    if target.evidence.get('herd_owner') not in (None, identity):
        raise ValueError('Feed resource goal has another owner')
    target.evidence['herd_owner'] = identity
    if count > 100000:
        assessment['blockers'].append('Seasonal feed requirement exceeds bounded stock planning limit')
        assessment['satisfied'] = False
        target.cancelled = True
        return
    target.target = {'resource': resource, 'quantity': count}
    if target.cancelled:
        target.status, target.reason = 'active', ''
        target.attempts += 1
        target.reopen_methods()
    target.cancelled = False
    goal.evidence['feed_goal'] = key
    assessment['feed_capacity'] = {'resource': resource, 'required_stock': count,
        'nutrition_per_item': selected['nutrition'] / selected['count'],
        'native_current_demand_per_day': sum(demand), 'future_births_credited': False}


async def husbandry_method(rt, identity):
    from .colony_skills import SkillBlocked, native
    from .strategic_state import fingerprint
    goal = rt.current_plan.colony_goals[identity]
    state = goal.evidence['husbandry']
    if not state['readable']:
        raise SkillBlocked('Husbandry: Native herd census unavailable')
    actions = []
    for row in state['training']:
        animal = row['animal']
        if row['wanted'] is True:
            continue  # Native learned state, not the checkbox, completes training.
        if goal.evidence['settings'].get(animal['id']) != animal['settingsToken']:
            raise SkillBlocked('Husbandry: Player animal settings changed; explicitly renew the herd target')
        actions.append(native('home/husbandry_config',
            **{k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')},
            animal=animal['id'], expected=animal['settingsToken'], census=animal['censusToken'], trainable=row['name']))
        break  # Recursive training can change other requested fields.
    if not actions:
        for animal in state['surplus'][:1]:
            if goal.evidence['settings'].get(animal['id']) != animal['settingsToken']:
                raise SkillBlocked('Husbandry: Player animal settings changed; explicitly renew the herd target')
            actions.append(native('home/husbandry_config',
                **{k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')},
                animal=animal['id'], expected=animal['settingsToken'], census=animal['censusToken'], slaughter=True))
    if actions:
        return 'herd-' + fingerprint(actions)[:16], actions
    if state['blockers']:
        raise SkillBlocked('Husbandry: ' + '; '.join(dict.fromkeys(state['blockers'])))
    return None
