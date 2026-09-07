"""Conditional crop timing from observed progress, including nights in the window."""
import math


def forecast(memory, observation):
    farm = observation.get('farm') or {}
    if farm.get('forecast_basis') != 'ideal_growth_days_v1':
        return {'unavailable': 'Native corrected crop observations unavailable'}
    tick = observation.get('game', {}).get('game_tick', 0)
    history = memory.setdefault('harvest_samples', {})
    rows = []
    present = set()
    for crop in farm.get('crop_types', []):
        name, count, remaining = crop.get('plant_def_name'), crop.get('total_plants'), crop.get('days_until_harvest')
        if not name or type(count) is not int or count < 0 or type(remaining) not in (float, int) or not math.isfinite(remaining) or remaining < 0:
            continue
        present.add(name)
        samples = history.setdefault(name, [])
        if samples and (tick < samples[-1]['tick'] or count != samples[-1]['count']
                        or remaining > samples[-1]['remaining'] + 1e-5 or tick-samples[-1]['tick'] > 15000):
            samples.clear()
        if not samples or tick-samples[-1]['tick'] >= 2500:
            samples.append({'tick': tick, 'count': count, 'remaining': remaining})
        samples[:] = samples[-49:]
        row = {'crop': name, 'plants': count, 'harvestable_plants': crop.get('harvestable_plants'),
               'harvestable_product_units': crop.get('expected_yield'),
               'mean_ideal_growth_days_remaining': remaining, 'mean_calendar_days_remaining': None}
        elapsed = tick-samples[0]['tick']
        if count == 0:
            row['unavailable'] = 'No planted crop; a zone is not a future harvest'
        elif crop.get('infected_count', 0):
            samples.clear()
            row['unavailable'] = 'Blight invalidates the stable-growth projection'
        elif remaining == 0:
            row['mean_calendar_days_remaining'] = 0.
        elif elapsed < 60000:
            row['unavailable'] = 'Needs a full game day of stable crop observations'
        else:
            rate = (samples[0]['remaining']-remaining)/(elapsed/60000)
            if rate > 1e-5:
                row['mean_calendar_days_remaining'] = round(remaining/rate, 2)
                row['window_days'] = round(elapsed/60000, 2)
            else:
                row['unavailable'] = 'No observed growth; inspect season and growing conditions'
        rows.append(row)
    for name in set(history)-present:
        del history[name]
    return {'crops': rows, 'scope': 'Mean time to harvest threshold if recent growth continues, not first harvest or food delivery. Excludes future weather, season changes, plant loss and harvest labor. Product units are not nutrition.'}
