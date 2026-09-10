"""Bound one exchange by current demand, eligible surplus and shared commitments."""
import math

from .colony_plan import TradeLine
from .production_policy import production_budgets


def economic_reserves(plan, buildings, policy=None):
    floors, stopped = production_budgets(plan)
    bases = {k: p.get('reserve', 0) for k, p in plan.control.get('resource_policy', {}).items()}
    targets = dict(bases)
    if policy is not None:
        for target in policy.targets:
            targets[target.item] = max(targets.get(target.item, 0), target.stock)
    for goal in plan.colony_goals.values():
        if not goal.cancelled and (resource := goal.target.get('resource')):
            targets[resource] = max(targets.get(resource, 0), number(goal.target.get('quantity')))
    for resource, target in targets.items():
        floors[resource] = floors.get(resource, 0) + target - bases.get(resource, 0)
    if not isinstance(buildings.get('resourceDeficit'), list):
        raise ValueError('Native construction commitments are unknown')
    for row in buildings['resourceDeficit']:
        count = number(row.get('stillNeeded'))
        floors[row['defName']] = floors.get(row['defName'], 0) + count
    return floors, stopped


def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ValueError('Economic stock or price evidence is unknown')
    return value


def select_trade(action, sheet, floors=None, stopped=()):
    """Explicit target order sets purchase priority; never create speculative bills."""
    policy = action.policy
    floors = floors or {}
    if sheet.get('omittedByRowCap') != 0 or sheet.get('omittedByFilter') != 0:
        raise ValueError('Economic selection requires an unfiltered complete trade sheet')
    balance = sheet.get('balance') or {}
    cash = number(balance.get('colonySilverNow'))
    trader_cash = number(balance.get('traderSilverNow'))
    reserve = max(policy.silver_reserve, floors.get('Silver', 0))
    budget = min(action.max_silver_spend, max(0, cash - reserve))
    if 'Silver' in stopped:
        budget = 0
    rows = sheet.get('rows')
    if not isinstance(rows, list):
        raise ValueError('Native trade inventory is unavailable')
    selected, evidence = [], []
    # Sales use only observed eligible surplus, capped by the buyer's current cash.
    # Purchases do not rely on anticipated sale proceeds or future production.
    for target in policy.targets:
        matches = [r for r in rows if r.get('defName') == target.item]
        if len(matches) != 1:
            evidence.append({'item': target.item, 'blocker': 'Unavailable or ambiguous native definition'})
            continue
        row = matches[0]
        if row.get('traderWillTrade') is not True or row.get('isPawn') is not False or row.get('isCurrency') is not False:
            evidence.append({'item': target.item, 'blocker': 'Ineligible trade row'})
            continue
        stock, supply = number(row.get('colonyCount')), number(row.get('traderCount'))
        floor = max(target.stock, floors.get(target.item, 0))
        count = 0
        if stock < floor and target.max_buy:
            price = number(row.get('buyPrice'))
            if 0 < price <= target.max_buy_price:
                count = int(min(floor - stock, supply, target.max_buy, math.floor(budget / price)))
                budget -= count * price
        elif stock > floor and target.max_sell and target.item not in stopped:
            # Classification comes from ThingDef, never localized names or model guesses.
            if row.get('protectedExport') is not False:
                evidence.append({'item': target.item, 'blocker': 'Protected equipment, food, medicine or unknown classification'})
                continue
            price = number(row.get('sellPrice'))
            if price > 0 and price >= target.min_sell_price:
                count = -int(min(stock - floor, target.max_sell, math.floor(trader_cash / price)))
                trader_cash += count * price
        evidence.append({'item': target.item, 'eligible_stock': stock, 'retained_target': floor,
                         'count': count, 'export_capacity': max(0, stock - floor)})
        if count:
            selected.append(TradeLine(item=target.item, count=count))
    return selected, evidence
