"""Paused native upkeep parity and the reference needs for Go replay."""
import math

from rimgovernor.colony_upkeep import evidence, progress_metric

SECTIONS = {'fires': 'MaintainFireSafety', 'items': 'SecureSupplies',
            'structures': 'MaintainEssentialRepairs', 'filth': 'MaintainCleanFacilities'}
FIELDS = {'fires': 'fires', 'items': 'vulnerable', 'structures': 'damaged', 'filth': 'filth'}


def audit_upkeep(colony, legacy):
    assert legacy['success'] and int(legacy['tick']) == int(colony['context']['tick'])
    raw, typed = legacy['upkeep'], colony['upkeep']['observed']
    assert raw['version'] == 1 and raw['tick'] == legacy['tick']
    issues = {r['field']: r for r in typed.get('issues', [])}
    result = {}
    reference = evidence(legacy)
    for section, goal in SECTIONS.items():
        rows = raw.get(section)
        projected = typed.get(section, [])
        if section in issues:
            assert not projected
            assert rows is None or section in raw.get('errors', {}) or len(rows) > 256, 'Unexpected unavailable upkeep census'
            result[goal] = {'need': 'unknown', 'priority': 4, 'targets': None, 'metric': None}
            continue
        assert isinstance(rows, list) and section not in raw.get('errors', {})
        assert len(rows) == len(projected) <= 256
        key = {'fires': 'fire', 'items': 'item', 'structures': 'building', 'filth': 'filth'}[section]
        ref = {r['id']: r for r in rows}
        seen = set()
        for row in projected:
            entity = row[key]['building'] if section == 'structures' else row[key]
            identity = entity['id']
            assert identity in ref and identity not in seen
            seen.add(identity)
            old = ref[identity]
            assert entity['mapId'] == colony['context']['identity']['mapId']
            assert entity['position'] == {'x': old['x'], 'z': old['z']}
            bools = ('roofed', 'inStorage', 'forbidden', 'medicine', 'perishable') if section == 'items' else ('home',)
            for field in bools:
                assert type(row[field]) is bool and row[field] == old[field]
            if section == 'items':
                assert entity['defName'] == old['defName'] and int(row['count']) == old['count']
                for field in ('deteriorationRate', 'baseDeteriorationRate'):
                    assert math.isclose(row[field], old[field], rel_tol=1e-6, abs_tol=1e-7)
                assert (int(row['rotTicks']) if 'rotTicks' in row else None) == old['rotTicks']
            elif section == 'structures':
                assert entity['defName'] == old['defName']
                assert row['building']['hitPoints'] == old['hitPoints'] and row['building']['maxHitPoints'] == old['maxHitPoints']
                assert row['repairPriority'] == old['repairPriority']
            elif section == 'fires':
                assert math.isclose(row['size'], old['size'], rel_tol=1e-6, abs_tol=1e-7)
            else:
                assert row['thickness'] == old['thickness'] and row.get('roomRole') == old['room']
        targets = reference[FIELDS[section]]
        assert targets is not None
        result[goal] = {'need': 'deficit' if targets else 'recovered', 'priority': 1 if section == 'fires' else 3,
                        'targets': [r['id'] for r in targets], 'metric': progress_metric(goal, targets)}
    return result


def audit_upkeep_review(active, expected):
    for goal, reference in expected.items():
        actual = active['goals'][goal]
        assert actual['Need'] == reference['need'] and actual['Priority'] == reference['priority'], (goal, actual, reference)
