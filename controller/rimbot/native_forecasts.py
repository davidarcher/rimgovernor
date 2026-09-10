"""Current-condition forecasts; unknown inputs never become zero or future stock."""
from math import isfinite

from .food_forecast import food_forecast


def finite(value):
    return value if type(value) in (int, float) and isfinite(value) else None


def power_forecast(buildings):
    nets = buildings.get('powerNets')
    if not isinstance(nets, list) or (buildings.get('powerSummary') or {}).get('readable') is False:
        return None
    result = []
    for net in nets:
        watts, stored = finite(net.get('netW')), finite(net.get('storedWd'))
        if stored is not None and stored < 0:
            stored = None
        result.append({'net_w': watts, 'stored_wd': stored,
            'days_at_current_deficit': stored / -watts if watts is not None and watts < 0 and stored is not None else None,
            'flags': net.get('flags', [])})
    return result


def forecasts(facts, buildings=None):
    inputs = facts.get('nativeForecastInputs') or {}
    known = inputs.get('readable') is True
    combined = food_forecast(inputs.get('combinedFoodSupply'))
    animal_ids = inputs.get('animalIds') if known else None
    animal_rows = ([row for row in combined['consumers'] if row['id'] in animal_ids]
                   if combined['readable'] and isinstance(animal_ids, list) else None)
    food = food_forecast(facts.get('foodSupply'))
    if 'nativeForecastInputs' in facts:
        human_ids = [row['id'] for row in (facts.get('foodSupply') or {}).get('consumers', [])]
        food = food_forecast(inputs.get('combinedFoodSupply'), consumer_ids=human_ids) if known and human_ids else {
            'readable': False, 'runwayDays': None, 'reason': 'Combined food demand or colonist census unavailable'}
    patients = []
    patient_rows = inputs.get('patients') if known else None
    for row in patient_rows or []:
        mood, target = finite(row.get('mood')), finite(row.get('moodTarget'))
        thresholds = [finite(row.get(key+'BreakThreshold')) for key in ('extreme', 'major', 'minor')]
        risk = None
        if mood is not None and all(value is not None for value in thresholds):
            risk = next((label for label, threshold in zip(('extreme', 'major', 'minor'), thresholds)
                         if mood <= threshold), 'none')
        patients.append(dict(row, breakRisk=risk,
                             moodPressure=None if mood is None or target is None else target-mood))
    crops = inputs.get('crops') if known else None
    construction = buildings or {}
    pending = None
    if construction.get('success') is True and isinstance(construction.get('buildings'), list):
        skipped = construction.get('skipped') or {}
        if not any(skipped.get(key) for key in ('byMatch', 'byStatus', 'byRadius')):
            pending = [row for row in construction['buildings'] if row.get('status') in ('blueprint', 'frame')]
    return {'observedTick': inputs.get('tick') if known else facts.get('tick'),
        'food': food,
        'animalFeed': {'readable': animal_rows is not None, 'consumers': animal_rows,
            'nutritionPerDay': sum(r['nutritionPerDay'] for r in animal_rows) if animal_rows is not None else None,
            'runwayDays': min((r['runwayDays'] for r in animal_rows), default=None) if animal_rows is not None else None},
        'harvest': {'readable': crops is not None, 'farms': facts.get('farms'), 'crops': crops,
                    'guaranteedNutrition': None},
        'labor': {'sowWork': total(crops, 'sowWork'), 'harvestWork': total(crops, 'harvestWork'),
                  'constructionWork': total(pending, 'workLeft'),
                  'completionDays': None},
        'people': patients if isinstance(patient_rows, list) else None,
        'power': power_forecast(buildings or {}),
        'assumptions': inputs.get('assumptions', [])}


def total(rows, key):
    if not isinstance(rows, list) or any(finite(row.get(key)) is None or row[key] < 0 for row in rows):
        return None
    return sum(row[key] for row in rows)
