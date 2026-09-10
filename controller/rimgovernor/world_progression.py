"""World outcome predicates over fresh, scoped native observations."""
from math import isfinite


def _number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and isfinite(value)


def caravan_cargo_held(plan, identity):
    step = next((s for s in plan.spec.steps if s.id == identity), None)
    progress = plan.progress.get(identity)
    return bool(step and progress and getattr(step.action, 'completion', None) == 'caravan_departed'
        and progress.issued.get('0') is not None
        and progress.issued['0'].get('cargo_departed') is not True)


def inventory_totals(items):
    totals = {}
    for item in items:
        if not _number(item.get('count')) or item['count'] < 0:
            raise ValueError('Native inventory count is unavailable')
        totals[item['defName']] = totals.get(item['defName'], 0) + item['count']
    return totals


async def reconcile_world(rt):
    from .colony_plan import Failure
    plan = rt.current_plan
    steps = [s for s in plan.spec.steps if (getattr(s.action, 'caravan_target', None) is not None or getattr(s.action, 'completion', None) == 'quest_completed')
             and (plan.progress[s.id].state == 'waiting' or caravan_cargo_held(plan, s.id))]
    if not steps:
        return
    world = await rt.game.query('home/world_progression', **({'includeStorage': True}
        if any(s.action.caravan_target and s.action.caravan_target.storage_cargo for s in steps) else {}))
    for step in steps:
        progress = plan.progress[step.id]
        receipt = progress.issued.get('0', {})
        target = step.action.caravan_target
        scope = {key: step.action.arguments.get(key) for key in ('colonyId', 'loadToken', 'mapId')}
        if step.action.completion == 'quest_completed':
            state = quest_outcome(world, step.action.arguments['questId'], scope=scope, issued_tick=receipt.get('issued_tick'))
            if state == 'complete':
                progress.state = 'complete'
                progress.failure = None
                receipt['completed_tick'] = world['ticksGame']
                rt.signal('plan.step_complete', {'step': step.id, 'quest': step.action.arguments['questId']})
            elif state in ('blocked', 'invalidated'):
                progress.state = 'blocked'
                progress.failure = Failure(code='quest_outcome_changed', detail='Native quest failed, expired or changed scope')
            continue
        outcome = caravan_outcome(world, scope=scope, pawn_ids=target.pawn_ids,
            destination=target.destination, issued_tick=receipt.get('issued_tick'),
            caravan_id=target.caravan_id or receipt.get('caravan_id'))
        complete = False
        if outcome['state'] in ('travelling', 'arrived'):
            receipt['caravan_id'] = outcome['caravan_id']
            if step.action.completion == 'caravan_departed':
                caravan = next(c for c in world['caravans'] if c['id'] == outcome['caravan_id'])
                cargo = {}
                for pawn in caravan['pawns']:
                    for item in pawn.get('inventory') or []:
                        cargo[item['defName']] = cargo.get(item['defName'], 0) + item['count']
                if all(cargo.get(resource, 0) >= count + target.carried_cargo.get(resource, 0)
                       for resource, count in target.cargo.items()):
                    receipt['cargo_departed'] = True
                    receipt['observed_cargo'] = cargo
                    receipt['confirmed'] = True
                    complete = True
            elif step.action.completion == 'caravan_arrived':
                complete = outcome['state'] == 'arrived'
        elif outcome['state'] == 'waiting' and step.action.completion == 'caravan_returned':
            roster = await rt.game.query('home/list_pawns', colonistsOnly=True, includeDead=True)
            people = {p.get('thingId'): p for p in roster.get('pawns', [])}
            complete = (not any(c.get('id') == target.caravan_id for c in world['caravans'])
                and all(identity in people and people[identity].get('dead') is False
                        and people[identity].get('downed') is False for identity in target.pawn_ids))
            if complete and target.storage_cargo:
                home = next((m for m in world.get('maps', []) if m['id'] == scope['mapId']), None)
                if home is None or not isinstance(home.get('storedItems'), list):
                    complete = False
                else:
                    stored = inventory_totals(home['storedItems'])
                    held = inventory_totals([i for p in home['pawns'] if p['thingId'] in target.pawn_ids for i in p['inventory']])
                    complete = all(held.get(r, 0) == 0 and stored.get(r, 0) >= count + target.stored_baseline.get(r, 0)
                        for r, count in target.storage_cargo.items())
                    if complete:
                        receipt['stored_cargo'] = target.storage_cargo
        if progress.state != 'waiting':
            continue  # Observation can release cargo holds without reviving cancelled work.
        if complete:
            progress.state = 'complete'
            progress.failure = None
            receipt['completed_tick'] = world['ticksGame']
            rt.signal('plan.step_complete', {'step': step.id, 'evidence': outcome})
        elif outcome['state'] in ('blocked', 'invalidated'):
            progress.state = 'blocked'
            progress.failure = Failure(code='world_outcome_changed', detail=outcome['reason'])
            rt.signal('plan.step_blocked', {'step': step.id, 'evidence': outcome})


def caravan_outcome(observed, *, scope, pawn_ids, destination, issued_tick, caravan_id=None):
    """A formation receipt or missing map pawn cannot certify world arrival."""
    if (observed.get('success') is not True or observed.get('complete') is not True
            or (observed.get('operation') or {}).get('ResultWasTruncated') is True):
        return {'state': 'unknown', 'reason': 'Complete native world observation is required'}
    if any(observed.get(key) != scope.get(key) or scope.get(key) is None
           for key in ('colonyId', 'loadToken', 'mapId')):
        return {'state': 'invalidated', 'reason': 'Colony, load or map changed'}
    tick = observed.get('ticksGame')
    if not _number(tick) or not _number(issued_tick) or tick <= issued_tick:
        return {'state': 'unknown', 'reason': 'A later native tick is required'}
    expected = set(pawn_ids)
    if not expected or len(expected) != len(pawn_ids):
        raise ValueError('Expected pawn IDs must be nonempty and unique')
    caravans = observed.get('caravans')
    if not isinstance(caravans, list):
        return {'state': 'unknown', 'reason': 'Caravan census is unavailable'}
    candidates = [row for row in caravans if expected & {p.get('thingId') for p in row.get('pawns', [])}]
    if not candidates:
        return {'state': 'waiting', 'reason': 'Expected pawns have not been observed in a caravan'}
    if len(candidates) != 1:
        return {'state': 'blocked', 'reason': 'Expected pawns are split across caravans'}
    caravan = candidates[0]
    if caravan_id is not None and caravan.get('id') != caravan_id:
        return {'state': 'invalidated', 'reason': 'Caravan identity changed'}
    pawns = caravan.get('pawns', [])
    if {p.get('thingId') for p in pawns} != expected:
        return {'state': 'blocked', 'reason': 'Caravan membership changed'}
    if any(p.get('dead') is True or p.get('downed') is True for p in pawns):
        return {'state': 'blocked', 'reason': 'Caravan member needs emergency review'}
    if any(p.get('dead') is not False or p.get('downed') is not False for p in pawns):
        return {'state': 'unknown', 'reason': 'Caravan member health is unavailable'}
    if caravan.get('destination') != destination:
        return {'state': 'invalidated', 'reason': 'Caravan route changed'}
    arrived = caravan.get('tile') == destination and caravan.get('moving') is False
    return {'state': 'arrived' if arrived else 'travelling', 'caravan_id': caravan.get('id'), 'tick': tick}


def quest_outcome(observed, quest_id, *, scope, issued_tick):
    """Only the native terminal success state demonstrates quest completion."""
    if (observed.get('success') is not True or observed.get('complete') is not True
            or (observed.get('operation') or {}).get('ResultWasTruncated') is True):
        return 'unknown'
    if any(observed.get(key) != scope.get(key) or scope.get(key) is None
           for key in ('colonyId', 'loadToken', 'mapId')):
        return 'invalidated'
    tick = observed.get('ticksGame')
    if not _number(tick) or not _number(issued_tick) or tick <= issued_tick:
        return 'unknown'
    if not isinstance(observed.get('quests'), list):
        return 'unknown'
    rows = [q for q in observed['quests'] if q.get('id') == quest_id]
    if len(rows) != 1:
        return 'unknown'
    state = rows[0].get('state')
    if state == 'EndedSuccess':
        return 'complete'
    if state in ('EndedFailed', 'EndedOfferExpired', 'EndedInvalid', 'EndedUnknownOutcome'):
        return 'blocked'
    return 'waiting' if state in ('NotYetAccepted', 'Ongoing') else 'unknown'


def survival_assessment(facts, *, reserve_days=15):
    """Evaluate current native supply and cold exposure; future harvest earns no credit.

    This is a readiness sample, not a guarantee about future weather or pawn jobs.
    Multi-day survival requires a separate continuous scoped sequence of living rosters.
    """
    from .food_forecast import food_forecast
    if not _number(reserve_days) or reserve_days <= 0:
        raise ValueError('Reserve horizon must be positive and finite')
    forecast = food_forecast(facts.get('foodSupply'))
    runway = forecast.get('runwayDays')
    count = facts.get('colonists')
    indoors = facts.get('indoorSleepingCapacity')
    low, high = facts.get('sleepingTemperatureMin'), facts.get('sleepingTemperatureMax')
    outside = facts.get('outdoorTemperature')
    checks = {
        'food_reserve': None if not _number(runway) else runway >= reserve_days,
        'indoor_sleeping': None if not _number(count) or not _number(indoors) else count > 0 and indoors >= count,
        'sleeping_temperature': None if not _number(low) or not _number(high) else 16 <= low <= high <= 28,
        'food_storage': facts.get('foodStorage') if type(facts.get('foodStorage')) is bool else None,
        'cold_exposure_observed': None if not _number(outside) else outside <= 0,
    }
    return dict(checks=checks, food_runway_days=runway, reserve_days=reserve_days,
        winter_readiness_observed=all(value is True for value in checks.values()),
        scope='Current cold-weather shelter and stored-food evidence; future survival is not guaranteed')
