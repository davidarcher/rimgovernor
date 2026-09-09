"""Conservative per-colonist runway under observed demand and rot deadlines."""
from math import inf, isfinite


def food_forecast(supply):
    """Split shared nutrition by demand; never lend another pawn's inventory.

    Each pawn consumes its allocated stocks in earliest-expiry order. This is a
    feasible food allocation under fixed temperature/access, not a prediction of
    actual job selection. Invalid or truncated inputs cannot establish runway.
    """
    try:
        if not isinstance(supply, dict) or supply.get('readable') is not True or supply.get('truncated'):
            raise ValueError('Food supply observation is unavailable or truncated')
        consumers = supply['consumers']
        demand = {}
        for row in consumers:
            identity, rate = row['id'], number(row['nutritionPerDay'])
            if not isinstance(identity, str) or not identity or identity in demand or rate < 0:
                raise ValueError('Invalid or duplicate food consumer')
            demand[identity] = rate
        total = sum(demand.values())
        if not total:
            raise ValueError('No positive native food demand')
        stocks, seen = [], set()
        for row in supply['stocks']:
            identity, amount, holder = row['id'], number(row['nutrition']), row['holder']
            if not isinstance(identity, str) or not identity or identity in seen or amount < 0:
                raise ValueError('Invalid or duplicate food stock')
            if holder is not None and holder not in demand:
                raise ValueError('Food holder is not an observed consumer')
            seen.add(identity)
            if row['perishable'] is True:
                expiry = number(row['rotTicks']) / 60000
                if expiry < 0:
                    raise ValueError('Negative rot deadline')
            elif row['perishable'] is False:
                expiry = inf
            else:
                raise ValueError('Food perishability is unavailable')
            stocks.append((expiry, identity, amount, holder))
        stocks.sort()
        rows = []
        for identity, rate in demand.items():
            if rate == 0:
                continue
            elapsed = consumed = allocated = 0.
            for expiry, _, amount, holder in stocks:
                if holder is not None and holder != identity:
                    continue
                share = amount if holder == identity else amount * rate / total
                allocated += share
                usable = min(share, max(0., expiry - elapsed) * rate)
                consumed += usable
                elapsed += usable / rate
            rows.append({'id': identity, 'runwayDays': elapsed, 'usableNutrition': consumed,
                         'allocatedNutrition': allocated, 'nutritionPerDay': rate})
        return {'readable': True, 'runwayDays': min(row['runwayDays'] for row in rows),
                'usableNutrition': sum(row['usableNutrition'] for row in rows),
                'atRiskNutrition': sum(row['allocatedNutrition'] - row['usableNutrition'] for row in rows),
                'inventoryNutrition': sum(amount for _, _, amount, holder in stocks if holder is not None),
                'consumers': rows, 'assumptions': supply.get('assumptions', [])}
    except (KeyError, TypeError, ValueError) as error:
        return {'readable': False, 'runwayDays': None, 'reason': str(error)}


def number(value):
    if type(value) not in (int, float) or not isfinite(value):
        raise ValueError('Native food quantity is unavailable')
    return value


def acquisition_targets(facts, target_days, *, limit=8):
    """Bound new harvest orders by observed nutrition deficit and pending yield.

    Outstanding designations limit additional acquisition but never count as
    stored food or clear the food goal. One indivisible plant may overshoot.
    """
    demand = number(facts.get('nutritionPerDay'))
    forecast = facts.get('foodForecast')
    if forecast is not None:
        if forecast.get('readable') is not True:
            raise ValueError('Food forecast is unavailable')
        deficit = sum(max(0., number(row['nutritionPerDay']) * target_days - number(row['usableNutrition']))
                      for row in forecast['consumers'])
    else:
        deficit = max(0., demand * target_days - number(facts.get('foodNutrition')))
    candidates = [p for p in facts.get('acquisition', []) if p.get('food')]
    outstanding = number(facts['pendingFoodNutrition']) if 'pendingFoodNutrition' in facts else sum(
        number(p.get('nutritionYield')) for p in candidates if p.get('designated'))
    needed = max(0., deficit - outstanding)
    selected, expected = [], 0.
    for plant in candidates:
        if plant.get('designated') or len(selected) >= limit or expected >= needed:
            continue
        amount = number(plant.get('nutritionYield'))
        if amount <= 0:
            continue
        selected.append(plant)
        expected += amount
    return selected, {'targetDays': target_days, 'neededNutrition': needed,
                      'outstandingNutrition': outstanding, 'selectedNutrition': expected}
