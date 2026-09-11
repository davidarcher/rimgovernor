"""Paused native parity checks for the typed routine-facts core and geometry."""
import math


async def prepare_food_stock(call):
    """Allow existing starting supplies before the read-only acceptance baseline."""
    facts = await call('food-setup-before', 'home/colony_facts', {'planning': False})
    assert facts.get('success') and facts['foodSupply']['readable']
    if facts['foodSupply']['stocks']:
        return {'allowed_cells': [], 'stocks': len(facts['foodSupply']['stocks'])}
    catalog = await call('food-setup-designators', 'rimworld/list_architect_designators', {'categoryId': 'Orders'})
    choices = [r for r in catalog.get('designators', []) if r.get('className') == 'RimWorld.Designator_Unforbid']
    assert len(choices) == 1, 'Native Unforbid designator unavailable'
    center = facts['center']
    cells = sorted(facts['forbiddenSupplies'], key=lambda c: (c['x']-center['x'])**2 + (c['z']-center['z'])**2)[:32]
    allowed = []
    for index, cell in enumerate(cells):
        await call('food-setup-allow-'+str(index), 'rimworld/apply_architect_designator',
                   {'designatorId': choices[0]['id'], 'x': cell['x'], 'z': cell['z'], 'keepSelected': False})
        allowed.append(cell)
        after = await call('food-setup-read-'+str(index), 'home/colony_facts', {'planning': False})
        assert after.get('success') and after['foodSupply']['readable']
        if after['foodSupply']['stocks']:
            return {'allowed_cells': allowed, 'stocks': len(after['foodSupply']['stocks'])}
    raise AssertionError('No accessible existing food stock in bounded starting-supply setup')


async def verify_colony_facts(wire, call, identity, context):
    legacy = await call('colony-legacy', 'home/colony_facts', {'planning': True})
    assert legacy.get('success') is True
    names = ['Wall', 'Bed', 'SleepingSpot', 'Plant_Rice', 'RimGovernor_MissingColonyDefinition']
    request = {'scope': {'expectedIdentity': identity}, 'planning': True,
               'requestedDefinitionNames': names}
    reply = await wire('colony-typed', 'observations_read_colony_facts', request)
    facts = reply.get('observed')
    assert isinstance(facts, dict), reply
    assert facts['context'] == context
    for old, new in [('colonists', 'colonistCount'), ('workers', 'workerCount'),
                     ('bedCapacity', 'bedCapacity'), ('indoorSleepingCapacity', 'indoorSleepingCapacity'),
                     ('foodStorage', 'foodStorage')]:
        assert facts[new] == legacy[old], (old, facts.get(new), legacy.get(old))
    for old, new in [('foodNutrition', 'foodNutrition'), ('nutritionPerDay', 'nutritionPerDay'),
                     ('foodRunwayDays', 'foodRunwayDays'), ('sleepingTemperatureMin', 'sleepingTemperatureMinC'),
                     ('sleepingTemperatureMax', 'sleepingTemperatureMaxC'), ('outdoorTemperature', 'outdoorTemperatureC')]:
        value = legacy.get(old)
        if value is None:
            assert new not in facts
        else:
            assert math.isclose(float(facts[new]), float(value), rel_tol=1e-5, abs_tol=1e-4), (old, facts.get(new), value)
    assert {q['defName']: int(q['units']) for q in facts.get('resources', [])} == legacy['resources']
    food, old_food = facts['foodSupply']['observed'], legacy['foodSupply']
    consumers = {r['pawnId']: r['nutritionPerDay'] for r in food.get('consumers', [])}
    assert set(consumers) == {r['id'] for r in old_food['consumers']}
    for row in old_food['consumers']:
        assert math.isclose(consumers[row['id']], row['nutritionPerDay'], rel_tol=1e-6, abs_tol=1e-6)
    stocks = {r['item']['id']: r for r in food.get('stocks', [])}
    assert stocks, 'Food stock parity requires populated native stock'
    assert set(stocks) == {r['id'] for r in old_food['stocks']}
    for old in old_food['stocks']:
        row = stocks[old['id']]
        assert row['item']['defName'] == old['defName'] and int(row['count']) == old['count']
        assert row.get('holderId') == old['holder'] and set(row['eaterIds']) == set(old['eaters'])
        assert row['perishable'] == old['perishable'] and row.get('roofed') == old.get('roofed')
        assert (int(row['rotTicks']) if 'rotTicks' in row else None) == old['rotTicks']
        for new, previous in [('nutrition', 'nutrition'), ('temperatureC', 'temperature')]:
            assert math.isclose(row[new], old[previous], rel_tol=1e-6, abs_tol=1e-6)
    census = food['completeness']
    assert census['page']['complete'] and int(census['returned']) == int(census['matched']) == len(consumers) + len(stocks)
    assert int(census['filtered']) == int(census['unreadable']) == 0
    forecast, old_forecast = facts['forecast']['observed'], legacy['nativeForecastInputs']
    assert set(forecast.get('animalIds', [])) == set(old_forecast['animalIds'])
    combined = forecast['combinedFoodSupply']
    combined_consumers = {r['pawnId']: r['nutritionPerDay'] for r in combined.get('consumers', [])}
    assert set(combined_consumers) == {r['id'] for r in old_forecast['combinedFoodSupply']['consumers']}
    for old in old_forecast['combinedFoodSupply']['consumers']:
        assert math.isclose(combined_consumers[old['id']], old['nutritionPerDay'], rel_tol=1e-6, abs_tol=1e-6)
    combined_stocks = {r['item']['id']: r for r in combined.get('stocks', [])}
    assert set(combined_stocks) == {r['id'] for r in old_forecast['combinedFoodSupply']['stocks']}
    for old in old_forecast['combinedFoodSupply']['stocks']:
        row = combined_stocks[old['id']]
        assert row.get('holderId') == old['holder'] and set(row['eaterIds']) == set(old['eaters'])
        assert row['item']['defName'] == old['defName'] and int(row['count']) == old['count']
        assert row['perishable'] == old['perishable'] and row.get('roofed') == old.get('roofed')
        assert (int(row['rotTicks']) if 'rotTicks' in row else None) == old['rotTicks']
        for new, previous in [('nutrition', 'nutrition'), ('temperatureC', 'temperature')]:
            assert math.isclose(row[new], old[previous], rel_tol=1e-6, abs_tol=1e-6)
    for section, identifier, fields in [
        ('crops', 'zoneId', [('crop','crop'),('sowWork','sowWork'),('harvestWork','harvestWork'),('maturePlants','maturePlants'),('stalledPlants','stalledPlants'),('standingYield','standingYield'),('product','product')]),
        ('patients', 'pawnId', [('bleedRatePerDay','bleedRatePerDay'),('hoursUntilDeathFromBloodLoss','hoursUntilDeathFromBloodLoss'),('mood','mood'),('moodTarget','moodTarget'),('minorBreakThreshold','minorBreakThreshold'),('majorBreakThreshold','majorBreakThreshold'),('extremeBreakThreshold','extremeBreakThreshold')]),
    ]:
        rows = {r[identifier]: r for r in forecast.get(section, [])}
        assert set(rows) == {str(r['id']) for r in old_forecast[section]}
        for old in old_forecast[section]:
            row = rows[str(old['id'])]
            for new, previous in fields:
                expected = old.get(previous)
                if type(expected) in (int, float):
                    assert math.isclose(row[new], expected, rel_tol=1e-6, abs_tol=1e-6)
                else:
                    assert row.get(new) == expected
    for section in ('upkeep', 'development'):
        assert facts[section]['unavailable']['reason'] == 'UNAVAILABLE_REASON_UNSUPPORTED'
    planning = facts['planning']['observed']
    definitions = {row['definition']['defName']: row for row in planning['definitions']}
    assert set(definitions) == set(names)
    for name in names[:-1]:
        row, old = definitions[name], legacy['definitions'][name]
        assert row['available'] == old['available']
        assert row.get('stuff') == old.get('stuff')
        assert {q['defName']: int(q['units']) for q in row.get('costs', [])} == old['costs']
        assert row['size'] == {'width': old['width'], 'height': old['height']}
        if old.get('growDays') is not None:
            for field in ('growDays', 'harvestNutrition', 'nutritionDemandPerDay'):
                assert math.isclose(row[field], old[field], rel_tol=1e-6, abs_tol=1e-6), field
    assert definitions[names[-1]]['available'] is False
    assert {issue['field'] for issue in definitions[names[-1]]['issues']} == {'costs', 'size'}
    cells = planning['cells']
    assert cells['context'] == context and cells['mapSize'] == legacy['mapSize']
    actual = {(row['cell']['x'], row['cell']['z']): row for row in cells['cells']}
    expected = {(row['x'], row['z']): row for row in legacy['cells']}
    assert set(actual) == set(expected)
    for point, row in actual.items():
        old = expected[point]
        for field in ('walkable', 'occupied', 'supportsLight', 'indoors', 'storageEmpty'):
            assert row[field] == old[field], (point, field)
        assert math.isclose(row['fertility'], old['fertility'], rel_tol=1e-5, abs_tol=1e-6)
        assert ('roof' in row) == old['roofed']
        assert ('zoneId' in row) == old['zone']
        assert row['fogged'] is False
    assert cells['completeness']['page']['complete'] is True
    assert int(cells['completeness']['returned']) == len(actual)
    for label, invalid in [
        ('missing', {}),
        ('duplicate', dict(request, requestedDefinitionNames=['Wall', 'Wall'])),
        ('unrequested', dict(request, planning=False)),
        ('limit', dict(request, page={'limit': 0})),
        ('cursor', dict(request, page={'cursor': 'not-supported'})),
        ('unknown', dict(request, unknown=True)),
    ]:
        refused = await wire('colony-'+label, 'observations_read_colony_facts', invalid)
        assert refused['failure']['code'] == 'FAILURE_CODE_INVALID_REQUEST', (label, refused)
    stale = dict(request, scope={'expectedIdentity': dict(identity, loadToken='old-load')})
    refused = await wire('colony-stale', 'observations_read_colony_facts', stale)
    assert refused['failure']['code'] == 'FAILURE_CODE_STALE_IDENTITY'
    plain = await wire('colony-no-planning', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}})
    assert plain['observed']['planning']['unavailable']['reason'] == 'UNAVAILABLE_REASON_NOT_REQUESTED'
    production = verify_production(facts, legacy)
    return {**production, 'cells_compared': len(actual), 'definitions_compared': len(names),
            'food_consumers_compared': len(consumers), 'food_stocks_compared': len(stocks),
            'combined_consumers_compared': len(combined_consumers), 'combined_stocks_compared': len(combined_stocks),
            'animals_compared': len(forecast.get('animalIds', [])), 'patients_compared': len(forecast.get('patients', [])),
            'structural_refusals': 6, 'stale_identity_refused': True, 'core_parity': True}


def verify_production(facts, legacy):
    farms = {f['zoneId']: f for f in facts.get('farms', [])}
    assert set(farms) == {str(f['id']) for f in legacy['farms']}
    for old in legacy['farms']:
        row = farms[str(old['id'])]
        assert row['crop'] == old['crop'] and row['edibleCrop'] == old['edible']
        for field in ('usableCells', 'plantedCells', 'growingCells'):
            assert int(row[field]) == old[field]
        for field in ('harvestLowerBoundDays', 'nutritionPerHarvestCell'):
            if old.get(field) is None:
                assert field not in row
            else:
                assert math.isclose(row[field], old[field], rel_tol=1e-6, abs_tol=1e-6)
    benches = {b['bench']['id']: b for b in facts.get('cooking', [])}
    assert set(benches) == {b['id'] for b in legacy['cooking']}
    for old in legacy['cooking']:
        row = benches[old['id']]
        assert row['bench']['defName'] == old['defName'] and row['bench']['position'] == old['position']
        assert row['usable'] == old['usable']
        assert {r['recipe']['defName'] for r in row.get('recipes', [])} == set(old['recipes'])
        assert [(b['recipe']['defName'], b['suspended']) for b in row.get('bills', [])] == [(b['recipe'], b['suspended']) for b in old['bills']]
    ready = any(b['usable'] and any(not bill['suspended'] and bill['recipe'] in b['recipes'] for bill in b['bills']) for b in legacy['cooking'])
    growing = sum(f['growingCells'] for f in legacy['farms'] if f['edible'])
    return {'farms_compared': len(farms), 'cooking_compared': len(benches), 'growing_cells': growing, 'cooking_ready': ready}
