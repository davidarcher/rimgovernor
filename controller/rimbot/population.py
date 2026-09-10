"""Explicit population commitments, native custody and ordinary recruitment labor."""
from .colony_skills import SkillBlocked, native
from .strategic_state import fingerprint


def goal_id(pawn):
    return 'Population-' + pawn


def capacity(policy, facts, snapshot, commitments, people):
    """Count accepted candidates once; prisoners consume supplies before admission."""
    admitted = {p['thingId'] for p in snapshot['people'] if p.get('admitted') is True and not p.get('dead')}
    dependents = {p['thingId'] for p in snapshot['people'] if p.get('guest') is True and not p.get('dead')}
    planned = admitted | dependents | set(commitments)
    if len(admitted | set(commitments)) > policy['maximum']:
        raise SkillBlocked('Population policy maximum would be exceeded')
    if facts.get('bedCapacity', 0) < len(admitted | set(commitments)):
        raise SkillBlocked('Provision colonist sleeping capacity before population commitments')
    demand = facts.get('nutritionPerDay')
    stock = facts.get('foodNutrition')
    if not admitted or not isinstance(demand, (int, float)) or demand <= 0 or not isinstance(stock, (int, float)):
        raise SkillBlocked('Population food capacity is unknown')
    # Candidate-specific nutrition is supplied by the native population read.
    by_id = {p['thingId']: p for p in snapshot['people']}
    extra = 0
    for identity in planned - admitted:
        amount = by_id.get(identity, {}).get('nutritionPerDay')
        if not isinstance(amount, (int, float)) or amount <= 0:
            raise SkillBlocked('Candidate food demand is unknown')
        extra += amount
    if stock < (demand + extra) * policy['food_days']:
        raise SkillBlocked('Provision the population policy food reserve before commitment')
    available = [p for p in people if p.get('dead') is False and p.get('downed') is False
                 and not p.get('drafted') and not p.get('mentalState')]
    for work in ('Doctor', 'Warden'):
        if not any(any(w.get('name') == work and w.get('disabled') is False and w.get('priority', 0) > 0
                       for w in (p.get('work') or {}).get('types', [])) for p in available):
            raise SkillBlocked('Population care requires an available assigned ' + work)
    return {'admitted': len(admitted), 'committed': len(planned - admitted),
            'foodRequired': (demand + extra) * policy['food_days']}


async def observe(rt):
    result = await rt.game.query('home/population')
    if result.get('success') is not True or not isinstance(result.get('people'), list):
        raise SkillBlocked('Native population observation unavailable')
    return result


async def refresh(rt, facts, people):
    plan = rt.current_plan
    goals = {key: g for key, g in plan.colony_goals.items() if key.startswith('Population-') and not g.cancelled}
    if not goals:
        return []
    token, direction = rt.context_token, rt.chat_revision
    snapshot = await observe(rt)
    await rt.ensure_context(token)
    if direction != rt.chat_revision:
        raise ValueError('Player direction changed during population observation')
    facts['population'] = snapshot
    by_id = {p['thingId']: p for p in snapshot['people']}
    admitted = {p['thingId'] for p in snapshot['people'] if p.get('admitted') is True and p.get('dead') is False}
    pending = {g.target['pawn'] for g in goals.values() if g.status != 'complete'}
    policy = plan.control.get('population_policy', {})
    if pending and len(admitted | pending) <= policy.get('maximum', 0):
        facts['populationHousingTarget'] = len(admitted | pending)
        consumers = pending | {p['thingId'] for p in snapshot['people'] if p.get('guest') and not p.get('dead')}
        demands = [by_id.get(key, {}).get('nutritionPerDay') for key in consumers - admitted]
        if all(isinstance(n, (int, float)) and n > 0 for n in demands) and facts.get('nutritionPerDay'):
            facts['populationNutritionPerDay'] = facts['nutritionPerDay'] + sum(demands)
            facts['populationFoodRunwayDays'] = facts.get('foodNutrition', 0) / facts['populationNutritionPerDay']
    workers = {p['thingId']: p for p in people}
    nodes = []
    for key, goal in goals.items():
        p = by_id.get(goal.target['pawn'])
        goal.evidence['population'] = p
        if p and p.get('admitted') is True:
            worker = workers.get(p['thingId'], {})
            equipment = (worker.get('equipment') or {}).get('primary')
            incapable = (worker.get('bio') or {}).get('incapableOfTags', [])
            housing = bool(p.get('ownedBed')) and p.get('ownedBedForPrisoners') is False
            integrated = housing and any(w.get('priority', 0) > 0 for w in (worker.get('work') or {}).get('types', []))
            integrated = integrated and (bool(equipment) or 'Violent' in incapable)
            goal.evidence['integration'] = {'housing': housing, 'work': worker.get('work'),
                                            'equipment': equipment, 'incapableOf': incapable}
            if integrated and p.get('needsTend') is False and p.get('food') is not None and p['food'] > .3:
                goal.status, goal.reason = 'complete', ''
                continue
        if goal.status == 'complete':
            # Completed admission is historical, not permission to recapture someone.
            continue
        signature = fingerprint({'pawn': {k: p.get(k) for k in ('admitted', 'prisoner', 'bed', 'ownedBed', 'resistance', 'needsTend')}
                                if p else None, 'beds': facts.get('bedCapacity')})
        if goal.evidence.get('population_signature') != signature:
            goal.last_progress_tick = facts['tick']
        if goal.status == 'blocked' and not goal.evidence.get('watchdog') and not any(plan.progress[s].state == 'blocked' for s in goal.steps):
            goal.status, goal.reason = 'active', ''
        goal.evidence['population_signature'] = signature
        nodes.append((key, 3))
    return nodes


async def guard(rt, identity, facts=None, people=None):
    plan = rt.current_plan
    goal = plan.colony_goals[identity]
    if goal.cancelled or goal.target.get('decision') == 'ignore':
        raise SkillBlocked('Population direction withdrawn')
    snapshot = await observe(rt)
    p = next((p for p in snapshot['people'] if p['thingId'] == goal.target['pawn']), None)
    if not p or p.get('dead') is not False:
        raise SkillBlocked('Candidate missing or dead; custody and recruitment are unverified')
    if p.get('prisoner') is True:
        expected = goal.target['interaction']
        for step in plan.spec.steps:
            if step.goal_id == identity and step.action.kind == 'native_operation' and step.action.tool == 'home/population':
                issued = plan.progress[step.id].issued.get('0', {})
                if issued.get('confirmed'):
                    expected = step.action.arguments['interaction']
        if p.get('interaction') != expected:
            raise SkillBlocked('Player prisoner interaction changed; explicit new direction required')
    if facts is None:
        facts = await rt.game.query('home/colony_facts', planning=True)
    if people is None:
        people = (await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, bio=True, equipment=True))['pawns']
    commitments = [g.target['pawn'] for key, g in plan.colony_goals.items()
                   if key.startswith('Population-') and not g.cancelled and g.status != 'complete']
    goal.evidence['capacity'] = capacity(plan.control['population_policy'], facts, snapshot, commitments, people)
    return p, snapshot, people


async def compile_method(rt, identity, facts, people):
    goal = rt.current_plan.colony_goals[identity]
    p, snapshot, people = await guard(rt, identity)
    if p.get('admitted'):
        worker = next((w for w in people if w['thingId'] == p['thingId']), {})
        if (worker.get('equipment') or {}).get('armed') is False and 'Violent' not in (worker.get('bio') or {}).get('incapableOfTags', []):
            if goal.method_seen('equip'):
                raise SkillBlocked('Recruit equipment order needs observed completion; no replay')
            weapons = await rt.game.query('home/list_things', category='weapons', ownership='ours',
                includeHeld=False, maxPositionsPerDef=12)
            for weapon in sorted(t['thingId'] for row in weapons.get('things', []) if row.get('oursUnforbidden', 0) > 0
                                 for t in row.get('positions', [])):
                args = dict(action='equip', pawn=p['thingId'], target=weapon, watch=False)
                preview = await rt.inspect_native('home/order', dict(args, dryRun=True))
                if preview.get('success') is True:
                    return 'equip', [dict(native('home/order', **args), completion='pawn_equipped')]
            raise SkillBlocked('Recruit equipment allocation lacks a native eligible available weapon')
        return None  # Shared work/shelter methods and native needs-driven bed claiming integrate the recruit.
    if p.get('prisoner'):
        if p.get('needsTend') is None or p.get('food') is None:
            raise SkillBlocked('Prisoner care state is unknown')
        if p['needsTend'] or p['food'] <= .3:
            goal.reason = 'Waiting for assigned doctor/warden care before recruitment'
            return None
        goal.reason = ''
        if goal.target['decision'] != 'recruit':
            if p.get('bed'):
                goal.status, goal.reason = 'complete', ''
            return None
        if p.get('recruitable') is not True:
            raise SkillBlocked('Native prisoner is not recruitable')
        mode = next((r['name'] for r in snapshot['interactions'] if r['name'] != 'MaintainOnly'), None)
        if not mode:
            raise SkillBlocked('Installed native recruitment interaction unavailable')
        if p['interaction'] == mode:
            return None
        return 'recruit-setting', [native('home/population', pawn=p['thingId'], interaction=mode,
                                         expectedInteraction=p['interaction'])]
    if p.get('guest') and p.get('bed'):
        if goal.target['decision'] == 'rescue' and p.get('needsTend') is False and (p.get('food') or 0) > .3:
            goal.status, goal.reason = 'complete', ''
            return None
        raise SkillBlocked('Rescued visitor is not a recruit; voluntary joining is owned by RimWorld')
    action = 'rescue' if goal.target['decision'] == 'rescue' else 'capture'
    if goal.method_seen(action):
        if any(w.get('job') == ('Capture' if action == 'capture' else 'Rescue')
               and (w.get('carriedThingId') == p['thingId'] or p.get('downed')) for w in people):
            return None
        raise SkillBlocked('Prior custody order requires observed delivery; no automatic replay')
    for worker in sorted(people, key=lambda p: p['thingId']):
        if worker.get('dead') or worker.get('downed') or worker.get('drafted') or worker.get('mentalState'):
            continue
        if worker.get('job') in ('Capture', 'Rescue', 'TendPatient'):
            continue
        args = dict(action=action, pawn=worker['thingId'], target=p['thingId'], watch=False)
        preview = await rt.inspect_native('home/order', dict(args, dryRun=True))
        if preview.get('success') is True:
            return action, [native('home/order', **args)]
    raise SkillBlocked('No native eligible worker, custody bed or reachable capture/rescue target')
