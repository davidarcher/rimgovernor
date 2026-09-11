"""Paused native parity checks for the typed routine-facts core and geometry."""
import math


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
    for section in ('foodSupply', 'forecast', 'upkeep', 'development'):
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
    return {'cells_compared': len(actual), 'definitions_compared': len(names),
            'structural_refusals': 6, 'stale_identity_refused': True, 'core_parity': True}
