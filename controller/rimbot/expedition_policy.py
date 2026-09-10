"""Deterministic expedition risk and quest evaluation over native evidence."""
from copy import deepcopy
from math import isfinite

from pydantic import Field, model_validator

from .colony_plan import Contract
from .food_forecast import food_forecast


class ExpeditionPolicy(Contract):
    minimum_home_colonists: int = Field(default=1, ge=1, le=100)
    minimum_home_food_days: float = Field(default=0.5, ge=0, le=60, allow_inf_nan=False)
    travel_food_margin_days: float = Field(default=0.5, ge=0, le=30, allow_inf_nan=False)
    maximum_travel_days: float = Field(default=3, gt=0, le=60, allow_inf_nan=False)
    maximum_caravans: int = Field(default=2, ge=1, le=20)
    minimum_goodwill: int = Field(default=-50, ge=-100, le=100)
    minimum_destination_temperature: float = Field(default=-10, ge=-100, le=50, allow_inf_nan=False)
    maximum_destination_temperature: float = Field(default=40, ge=-50, le=100, allow_inf_nan=False)
    keep_home_doctor: bool = True
    require_return_storage: bool = True

    @model_validator(mode='after')
    def temperature_range(self):
        if self.minimum_destination_temperature > self.maximum_destination_temperature:
            raise ValueError('Destination temperature limits are reversed')
        return self


def number(value):
    return type(value) in (int, float) and isfinite(value)


def policy_for(plan):
    return ExpeditionPolicy.model_validate(plan.control.get('expedition_policy', {}))


def home_food_after(facts, crew, cargo):
    supply = deepcopy(facts.get('foodSupply'))
    if not isinstance(supply, dict) or supply.get('readable') is not True:
        return None
    if food_forecast(supply).get('readable') is not True:
        return None
    people = [p for p in supply.get('consumers', []) if p.get('id') not in crew]
    ids = {p['id'] for p in people}
    supply['consumers'] = people
    stocks = []
    for row in supply.get('stocks', []):
        if row.get('holder') in crew:
            continue
        row['eaters'] = [p for p in row.get('eaters', []) if p in ids]
        if row['eaters']:
            stocks.append(row)
    # Removing the longest-lived supplies first is conservative for the home runway.
    stocks.sort(key=lambda r: r.get('rotTicks', 0) if r.get('perishable') else float('inf'), reverse=True)
    remaining = dict(cargo)
    for row in stocks:
        count, nutrition = row.get('count'), row.get('nutrition')
        if not number(count) or count <= 0 or not number(nutrition):
            return None
        take = min(count, remaining.get(row.get('defName'), 0))
        remaining[row.get('defName')] = remaining.get(row.get('defName'), 0) - take
        row['nutrition'] = nutrition * (count - take) / count
    supply['stocks'] = stocks
    return food_forecast(supply).get('runwayDays')


async def guard_population_commitments(rt, crew, cargo, facts):
    """Keep B22 care and provision commitments available at the departing home."""
    commitments = {g.target['pawn'] for key, g in rt.current_plan.colony_goals.items()
        if key.startswith('Population-') and not g.cancelled and g.status != 'complete'}
    if not commitments:
        return
    crew = set(crew)
    if crew & commitments:
        raise ValueError('Complete the selected pawn population commitment before departure')
    from .population import capacity, observe
    snapshot = await observe(rt)
    people = (await rt.game.query('home/list_pawns', colonistsOnly=True, work=True))['pawns']
    policy = rt.current_plan.control.get('population_policy', {})
    if not policy:
        raise ValueError('Committed population capacity policy is unavailable')
    capacity(policy, facts, snapshot, commitments, people)
    runway = home_food_after(facts, crew, cargo)
    consumers = [p for p in facts.get('foodSupply', {}).get('consumers', []) if p['id'] not in crew]
    demand = sum(p['nutritionPerDay'] for p in consumers)
    if runway is None or not number(demand) or demand <= 0:
        raise ValueError('Remaining home food capacity is unknown')
    remaining = dict(facts, nutritionPerDay=demand, foodNutrition=runway * demand)
    snapshot = dict(snapshot, people=[p for p in snapshot['people'] if p['thingId'] not in crew])
    capacity(policy, remaining, snapshot, commitments, [p for p in people if p['thingId'] not in crew])


def evaluate_expedition(policy, preview, facts, world, *, action, crew=(), cargo=None):
    """An explicit return can recover a short-supplied party; risk stays visible."""
    blocked, warnings = [], []
    route = preview.get('route', {})
    if world.get('success') is not True or world.get('complete') is not True or (world.get('operation') or {}).get('ResultWasTruncated'):
        blocked.append('Complete world census is required')
    if action == 'stop':
        return dict(eligible=not blocked, blockers=blocked, warnings=warnings)
    if route.get('reachable') is not True:
        blocked.append('Native route is unavailable')
    ticks, food, temperature = route.get('estimatedTicks'), route.get('foodDays'), route.get('temperature')
    days = ticks / 60000 if number(ticks) and ticks >= 0 else None
    if days is None:
        blocked.append('Native travel time is unknown')
    elif days > policy.maximum_travel_days and action != 'return':
        blocked.append('Travel exceeds the player time budget')
    required = None if days is None else days * (1 if action == 'return' else 2) + policy.travel_food_margin_days
    if required is None or not number(food) or food < required:
        (warnings if action == 'return' else blocked).append('Travel food does not cover the route and reserve margin')
    if not number(temperature) or not policy.minimum_destination_temperature <= temperature <= policy.maximum_destination_temperature:
        (warnings if action == 'return' else blocked).append('Destination temperature is outside the player limits or unknown')
    if route.get('hostile') is True:
        blocked.append('Hostile settlement visits require separate player combat direction')
    goodwill = route.get('goodwill')
    if route.get('factionId') and (not number(goodwill) or goodwill < policy.minimum_goodwill) and action != 'return':
        blocked.append('Diplomatic relations do not meet the player policy')
    storage = preview.get('returnStorage')
    if policy.require_return_storage and (not isinstance(storage, list) or any(not number(r.get('cells')) or r['cells'] <= 0 for r in storage)):
        (warnings if action == 'return' else blocked).append('Return cargo lacks an accepting storage area')
    runway = None
    if action == 'form':
        if len(preview.get('homePawns', [])) < policy.minimum_home_colonists:
            blocked.append('Home staffing would fall below player policy')
        if policy.keep_home_doctor and preview.get('homeDoctors', 0) < 1:
            blocked.append('No capable doctor would remain at home')
        if len(world.get('caravans', [])) + len(world.get('assemblies', [])) >= policy.maximum_caravans:
            blocked.append('Concurrent expedition limit reached')
        runway = home_food_after(facts, set(crew), cargo or {})
        if runway is None or runway < policy.minimum_home_food_days:
            blocked.append('Home food runway would fall below player policy or is unknown')
    return dict(eligible=not blocked, blockers=blocked, warnings=warnings, estimated_days=days,
                travel_food_days=food, required_travel_food_days=required, home_food_days=runway,
                uncertainty='Native estimates exclude future incidents, weather changes and uncompleted production')


def evaluate_world(policy, world, facts):
    if (world.get('success') is not True or world.get('complete') is not True
            or (world.get('operation') or {}).get('ResultWasTruncated') is True):
        return dict(readable=False, caravans=[], quests=[], reason='Complete native world evidence is required')
    parties = []
    for caravan in world.get('caravans', []):
        routes = [r for r in caravan.get('homeRoutes', []) if r.get('reachable') is True and number(r.get('estimatedTicks')) and r['estimatedTicks'] >= 0]
        home = min(routes, key=lambda r: r['estimatedTicks'], default=None)
        food = caravan.get('foodDays')
        health = bool(caravan.get('pawns')) and all(p.get('dead') is False and p.get('downed') is False for p in caravan.get('pawns', []))
        stranded = not home or not number(food) or food < home['estimatedTicks'] / 60000 + policy.travel_food_margin_days
        parties.append(dict(id=caravan['id'], recovery_required=stranded or not health,
            reachable_home=home, food_days=food, healthy=health,
            recommendation='Player review: hold, resupply or explicit return' if stranded or not health else 'Observed return route available'))
    quests = []
    resources = facts.get('resources', {})
    for quest in world.get('quests', []):
        requirements = quest.get('tradeRequests', [])
        needed = {}
        for row in requirements:
            if row.get('resource') and number(row.get('count')) and row['count'] > 0:
                needed[row['resource']] = needed.get(row['resource'], 0) + row['count']
        deficits = {resource: max(0, count - resources.get(resource, 0)) for resource, count in needed.items()}
        terminal = quest.get('state', '').startswith('Ended')
        from .world_progression import inventory_totals
        carried = []
        for caravan in world.get('caravans', []):
            inventory = inventory_totals([i for p in caravan.get('pawns', []) for i in (p.get('inventory') or [])])
            if (needed and all(r.get('resource') and number(r.get('count')) and r['count'] > 0 for r in requirements)
                    and all(inventory.get(resource, 0) >= count for resource, count in needed.items())):
                carried.append(caravan['id'])
        quests.append(dict(id=quest['id'], state=quest.get('state'), native_eligible=quest.get('canAccept') is True,
            resource_deficits=deficits, resource_scope='Current home stock; carried cargo is listed separately',
            carried_candidates=carried, objectives=requirements, rewards=quest.get('rewardChoices', []),
            recommendation='Terminal objective; do not replay' if terminal else
                'Cargo observed in listed parties; validate native quality, freshness and settlement eligibility' if carried else
                'Production or acquisition required; no future output credited' if any(deficits.values()) else
                'Explicit player choice required; acceptance is separate from completion'))
    return dict(readable=True, caravans=parties, quests=quests, policy=policy.model_dump(),
                scope='Read-only evaluation; no automatic quest acceptance, diplomatic escalation or expedition orders')


def validate_quest_spending(plan, preview, world, quest_id):
    if preview.get('accepted') is not True:
        raise ValueError(preview.get('reason') or 'Native quest fulfillment is unavailable')
    rule = plan.control.get('resource_policy', {}).get(preview['resource'], {})
    if rule.get('spending') in ('stop', 'defense_only'):
        raise ValueError('Player resource policy prevents quest cargo spending')
    if not number(preview.get('available')) or preview['available'] < preview['count'] + rule.get('reserve', 0):
        raise ValueError('Quest fulfillment would consume the carried resource reserve')
    quest = next((q for q in world.get('quests', []) if q['id'] == quest_id), None)
    if world.get('complete') is not True or quest is None or quest.get('state') != 'Ongoing':
        raise ValueError('Current ongoing quest evidence is required')
    parts = quest.get('tradeRequests', [])
    if len(parts) != 1 or parts[0].get('settlementId') != preview.get('settlementId'):
        raise ValueError('Native quest settlement changed')
    faction = next((f for f in world.get('factions', []) if f['id'] == parts[0].get('factionId')), None)
    if (faction is None or faction.get('hostile') is not False or not number(faction.get('goodwill'))
            or faction['goodwill'] < policy_for(plan).minimum_goodwill):
        raise ValueError('Quest diplomacy no longer meets player policy')


def validate_gift(plan, preview):
    if preview.get('accepted') is not True:
        raise ValueError(preview.get('reason') or 'Native gift is unavailable')
    rule = plan.control.get('resource_policy', {}).get('Silver', {})
    if rule.get('spending') in ('stop', 'defense_only'):
        raise ValueError('Player policy prevents diplomatic silver spending')
    if not number(preview.get('available')) or preview['available'] < preview['silver'] + rule.get('reserve', 0):
        raise ValueError('Gift would consume the carried silver reserve')
    if not number(preview.get('goodwill')) or preview['goodwill'] < policy_for(plan).minimum_goodwill:
        raise ValueError('Diplomacy does not meet player expedition policy')
