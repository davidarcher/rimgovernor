"""Field sizing is a production budget, never a credit to stored nutrition."""
from math import ceil, isfinite


def field_target(facts, target_days=7):
    rice = facts.get('definitions', {}).get('Plant_Rice', {})
    values = (facts.get('nutritionPerDay'), rice.get('growDays'),
              rice.get('harvestNutrition'), target_days)
    if any(isinstance(v, bool) or not isinstance(v, (int, float))
           or not isfinite(v) or v <= 0 for v in values):
        return None
    demand, grow_days, nutrition, target_days = values
    # The existing 2.5 growth allowance covers a planning cycle. A larger
    # player reserve requires enough yield to replenish that reserve per cycle.
    return max(facts.get('colonists', 0) * 10,
               ceil(demand * max(grow_days * 2.5, target_days) / nutrition))


def growing_cells(facts):
    return sum(f.get('growingCells', 0) for f in facts.get('farms', [])
               if f.get('edible') is True)
