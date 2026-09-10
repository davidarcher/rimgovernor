"""Replayable colony priorities, completion predicates and resource accounting."""
from dataclasses import dataclass
from math import ceil


@dataclass(frozen=True)
class ColonyPolicy:
    execution_speed: str = 'Normal'
    food_min_days: float = 3
    food_target_days: float = 7
    foothold_food_days: float = 3
    temperature_enter_low: float = 12
    temperature_exit_low: float = 16
    temperature_enter_high: float = 32
    temperature_exit_high: float = 28
    wood_min: int = 120
    wood_target: int = 350
    wood_max: int = 500
    wood_reserve: int = 30
    max_method_attempts: int = 3
    blocked_after_ticks: int = 60000
    max_development_projects: int = 2

    def __post_init__(self):
        if type(self.max_development_projects) is not int or not 1 <= self.max_development_projects <= 8:
            raise ValueError('Development project limit must be an integer from 1 through 8')
        if self.execution_speed not in ('Normal', 'Fast', 'Superfast'):
            raise ValueError('Use a normal native game speed')
        if not 0 < self.food_min_days < self.food_target_days:
            raise ValueError('Food entry threshold must be below its recovery target')
        if not 0 <= self.wood_min < self.wood_target <= self.wood_max:
            raise ValueError('Resource thresholds must be ordered')


def latch(latches, name, value, enter, exit, *, high=False):
    """Unknown observations retain risk; they never prove recovery."""
    active = latches.get(name, False)
    if value is not None:
        active = (value > enter if high else value < enter) if not active else (
            value >= exit if high else value <= exit)
    latches[name] = active
    return active


def derive(batch, native, policy):
    """Combine fresh domain facts with the existing native observation contract."""
    value = dict(native)
    from .native_forecasts import forecasts, power_forecast
    value['forecasts'] = forecasts(native, batch.native.get('buildings', {}))
    if 'foodSupply' in native:
        forecast = value['forecasts']['food']
        value['foodForecast'] = forecast
        value['rawFoodRunwayDays'] = native.get('foodRunwayDays')
        value['foodRunwayDays'] = forecast['runwayDays']
    people = batch.summary.pawns
    value['medicalKnown'] = all(p.bleeding is not None and getattr(p,'needs_tend',None) is not None for p in people if not p.dead)
    value['criticalPatients'] = [p.thing_id for p in people if not p.dead and (p.downed or p.bleeding or p.needs_tend)]
    threats=batch.native.get('status_after',{}).get('threats',{})
    incapacitated={p['thingId'] for p in threats.get('hostiles',[])
        if p.get('thingId') and p.get('downed') is True}
    # The native faction census includes downed raiders. Remove only identities
    # positively observed incapacitated; unlisted/truncated threats retain risk.
    value['hostiles'] = max(0,batch.summary.hostile_count-len(incapacitated)) + batch.summary.hunting_predator_count
    value['armed'] = sum(p.armed is True and not p.downed and not p.dead for p in people)
    buildings = batch.native.get('buildings', {})
    nets = power_forecast(buildings)
    value['powerHeadroom'] = None if nets is None or any(n['net_w'] is None for n in nets) else (
        min((n['net_w'] for n in nets), default=0))
    value['constructionDeficit'] = {r['defName']: r['stillNeeded'] for r in buildings.get('resourceDeficit', [])}
    value['powerRequired'] = any((b.get('defName') or b.get('buildDefName')) in ('Heater', 'Cooler')
                                 for b in buildings.get('buildings', []))
    zones = batch.native.get('zones', {}).get('zones', [])
    value['foodStorage'] = native.get('foodStorage') is True
    value['workCoverage'] = False  # The work skill replaces this after fresh native work readback.
    return value


def criteria(facts, policy):
    count = facts.get('colonists', 0)
    low, high = facts.get('sleepingTemperatureMin'), facts.get('sleepingTemperatureMax')
    food = facts.get('foodRunwayDays')
    return {
        'sleeping': count > 0 and facts.get('bedCapacity', 0) >= count,
        'shelter': count > 0 and facts.get('indoorSleepingCapacity', 0) >= count,
        'food': food is not None and food >= policy.foothold_food_days,
        'production': count > 0 and sum(f.get('growingCells', 0) for f in facts.get('farms', [])
                                       if f.get('edible') is True) >= count * 10,
        'storage': facts.get('foodStorage') is True,
        'cooking': any(b.get('usable') is True and any(not bill.get('suspended', True)
                       and bill.get('recipe') in b.get('recipes', []) for bill in b.get('bills', []))
                       for b in facts.get('cooking', [])),
        'temperature': low is not None and high is not None and low >= policy.temperature_enter_low
                       and high <= policy.temperature_enter_high,
        'power': not facts.get('powerRequired', True) or (facts.get('powerHeadroom') is not None
                                                         and facts['powerHeadroom'] >= 0),
        'medical': facts.get('medicalKnown') is True and facts.get('criticalPatients') == [],
        'defense': facts.get('hostiles') == 0 and facts.get('armed', 0) >= min(2, count),
        'work': facts.get('workCoverage') is True and not facts.get('cleanupPawns') and not facts.get('colonyNaming'),
    }


def priority_nodes(facts, latches, policy):
    gates = criteria(facts, policy)
    food_risk = latch(latches, 'food', facts.get('foodRunwayDays'), policy.food_min_days, policy.food_target_days)
    cold = latch(latches, 'cold', facts.get('sleepingTemperatureMin') if facts.get('sleepingTemperatureMin') is not None else facts.get('outdoorTemperature'),
                 policy.temperature_enter_low, policy.temperature_exit_low)
    hot = latch(latches, 'hot', facts.get('sleepingTemperatureMax') if facts.get('sleepingTemperatureMax') is not None else facts.get('outdoorTemperature'),
                policy.temperature_enter_high, policy.temperature_exit_high, high=True)
    wood = latch(latches, 'wood', facts.get('resources', {}).get('WoodLog', 0), policy.wood_min, policy.wood_target)
    nodes = []
    if facts.get('colonyNaming'): nodes.append(('ConfirmColonyNames',0))
    if facts.get('hostiles', 0): nodes.append(('ActiveCombat', 0))
    if not gates['medical']: nodes.append(('CriticalMedical', 1))
    if not facts.get('hostiles') and facts.get('cleanupPawns'): nodes.append(('RestoreWorkers', 1))
    if facts.get('forbiddenSupplies'): nodes.append(('AllowStartingSupplies', 2))
    if not gates['work']: nodes.append(('EnsureWorkAssignments', 2))
    if food_risk or not gates['food'] or not gates['production']: nodes.append(('EnsureFoodSupply', 2))
    if not gates['shelter'] or not gates['sleeping']: nodes.append(('EnsureInitialShelter', 2))
    if cold or hot or not gates['temperature']: nodes.append(('EnsureTemperatureSafety', 2))
    if not gates['cooking']: nodes.append(('EnsureCooking', 2))
    if not gates['power']: nodes.append(('EnsureBasicPower', 2))
    if not gates['storage']: nodes.append(('EnsureFoodStorage', 3))
    if not gates['defense']: nodes.append(('EnsureBasicDefense', 3))
    if wood: nodes.append(('MaintainWood', 3))
    from .gear_upkeep import needs_upkeep
    if needs_upkeep(facts.get('gearUpkeep')): nodes.append(('MaintainEquipment', 3))
    return nodes


def allocation(plan, facts, proposed, policy, *, survival=False):
    """Account for native outstanding deficits and all accepted unissued slots."""
    reserved = dict(facts.get('constructionDeficit', {}))
    for step_id, slots in plan.control.get('costs', {}).items():
        progress = plan.progress.get(step_id)
        if progress is None or progress.state in ('cancelled', 'complete', 'blocked'):
            continue
        for slot, costs in slots.items():
            if progress.issued.get(slot, {}).get('confirmed'):
                continue  # Now included in the native blueprint/frame deficit.
            for resource, amount in costs.items():
                reserved[resource] = reserved.get(resource, 0) + amount
    reserves = {} if survival else {'WoodLog': policy.wood_reserve}
    stock = facts.get('resources', {})
    return {resource: max(0, amount + reserved.get(resource, 0) + reserves.get(resource, 0)
                             - stock.get(resource, 0)) for resource, amount in proposed.items()
            if amount + reserved.get(resource, 0) + reserves.get(resource, 0) > stock.get(resource, 0)}


def work_assignment(pawns, required_work=None, overrides=None):
    """Greedy coverage with stable tie breaks and a load penalty for specialists."""
    skill_for = {'Hunting': 'Shooting', 'Doctor': 'Medicine', 'Cooking': 'Cooking', 'Construction': 'Construction',
                 'Growing': 'Plants', 'PlantCutting': 'Plants'}
    skill_for.update(required_work or {})
    available = [p for p in pawns if not p.get('dead') and not p.get('downed') and not p.get('drafted') and not p.get('mentalState')
                 and (p.get('work') or {}).get('applies') is True]
    result = {p['thingId']: {} for p in available}
    load = {p['thingId']: 0 for p in available}
    owners = {}
    for work, skill in skill_for.items():
        candidates = []
        for pawn in available:
            if (overrides or {}).get(pawn['thingId'], {}).get(work) == 0: continue
            if work == 'Hunting' and ((pawn.get('equipment') or {}).get('primary') or {}).get('ranged') is not True: continue
            types = {w['name']: w for w in pawn['work'].get('types', [])}
            if work not in types or types[work].get('disabled') is not False:
                continue
            skills = {s['name']: s for s in (pawn.get('bio') or {}).get('skills', [])}
            value = skills.get(skill, {}) if skill else {'level': 0}
            if value.get('level') is None or value.get('disabled'):
                continue
            score = value['level'] + {'Minor': 2, 'Major': 4}.get(value.get('passion'), 0) - 3 * load[pawn['thingId']]
            candidates.append((load[pawn['thingId']], -score, pawn['thingId']))
        if candidates:
            owner = min(candidates)[2]
            owners[work] = owner
            load[owner] += 1
    hunters = {owners['Hunting']} if 'Hunting' in owners else set()
    second_hunters = [p for p in available if p['thingId'] not in hunters
        and ((p.get('equipment') or {}).get('primary') or {}).get('ranged') is True
        and any(w['name']=='Hunting' and w.get('disabled') is False for w in p['work']['types'])]
    if second_hunters:
        hunters.add(min(second_hunters,key=lambda p:(load[p['thingId']],p['thingId']))['thingId'])
    growers = {owners['Growing']} if 'Growing' in owners else set()
    spare_growers = []
    for pawn in available:
        if load[pawn['thingId']]: continue
        capable = any(w['name']=='Growing' and w.get('disabled') is False for w in pawn['work']['types'])
        skill = next((s for s in (pawn.get('bio') or {}).get('skills',[]) if s['name']=='Plants'),{})
        if capable and skill.get('level') is not None and not skill.get('disabled'):
            spare_growers.append((-skill['level'],pawn['thingId']))
    if spare_growers: growers.add(min(spare_growers)[1])
    for pawn in available:
        identity = pawn['thingId']
        manual = pawn['work'].get('manualPriorities') is True
        for entry in pawn['work'].get('types', []):
            work = entry['name']
            if entry.get('disabled') is not False:
                continue
            if work in ('Firefighter', 'Patient', 'BedRest', 'PatientBedRest', 'Childcare'):
                result[identity][work] = 1
            elif work in owners:
                primary = identity in growers if work=='Growing' else identity in hunters if work=='Hunting' else owners[work]==identity
                result[identity][work] = 1 if primary else (3 if manual else 0)
            elif work in ('Hauling', 'Cleaning', 'BasicWorker'):
                result[identity][work] = 3
            else:
                result[identity][work] = 0
        # Checkbox mode has no ranking. Limit specialist jobs instead of pretending
        # that stored 1..4 priorities change the game's effective order.
    return result, all(work in owners for work in {'Doctor', 'Cooking', 'Construction', 'Growing', *(required_work or {})})


def farm_patches(layout):
    return layout['farms'] if 'farms' in layout else [layout['farm']] if layout.get('farm') else []


def starter_layouts(facts):
    """Rank shelter sites, then fit several nearby fertile field patches."""
    rice = facts.get('definitions', {}).get('Plant_Rice', {})
    yield_, grow_days = rice.get('harvestNutrition'), rice.get('growDays')
    cells = {(c['x'], c['z']): c for c in facts.get('cells', [])}
    anchor = facts['center']
    target = ceil(facts.get('nutritionPerDay', 0) * grow_days * 2.5 / yield_) if yield_ and grow_days else 0
    def points(x, z, width, height):
        return {(a, b) for a in range(x, x+width) for b in range(z, z+height)}
    def free(p):
        c = cells.get(p, {})
        return c.get('walkable') is True and not c.get('occupied') and not c.get('zone')
    rooms = []
    for x, z in sorted(cells):
        footprint = points(x, z, 9, 9)
        if not all(free(p) and cells[p].get('supportsLight') is True for p in footprint): continue
        margin = points(x, z-4, 9, 3)
        distance = (x+4-anchor['x'])**2 + (z+4-anchor['z'])**2
        rooms.append((distance + 3*sum(not free(p) for p in margin), x, z))
    layouts = []
    for score, x, z in sorted(rooms)[:24]:
        reserved = points(x-1, z-1, 11, 11) | points(x, z-5, 9, 4)
        patches, chosen = [], set()
        farmland = sorted(cells, key=lambda p: ((p[0]-x-4)**2+(p[1]-z-4)**2, p))
        for size in (4,3,2,1):
            if len(chosen)>=target or len(patches)>=32:break
            for a,b in farmland:
                patch=points(a,b,size,size)
                if patch & (reserved|chosen):continue
                if not all(free(p) and cells[p].get('fertility',0)>=rice.get('fertilityMin',1) for p in patch):continue
                patches.append({'x':a,'z':b,'width':size,'height':size})
                chosen|=patch
                if len(chosen)>=target or len(patches)>=32:break
        # A reduced initial field is useful on constrained maps, but its actual
        # production remains a measured goal rather than a promised future harvest.
        penalty = max(0,target-len(chosen))*2
        layout = {'room':{'x':x,'z':z,'width':9,'height':9}, 'farm':patches[0] if patches else None, 'farms':patches,
                  'storage':{'x':x+3,'z':z+5,'width':3,'height':3}}
        layouts.append((score+penalty,x,z,layout))
    return [row[-1] for row in sorted(layouts,key=lambda row:row[:3])[:12]]
