"""Maintain bounded native Home coverage only for exact autonomous facilities."""
from .colony_plan import Zone
from .construction_ownership import owned_buildings
from .strategic_state import fingerprint


def targets(plan, facts):
    if plan is None:
        return []
    owned = owned_buildings(plan, facts)
    if owned is None:
        return None
    zones = {}
    for step in plan.spec.steps:
        goal = plan.colony_goals.get(step.goal_id)
        progress = plan.progress[step.id]
        if step.source != 'AUTOPILOT' or goal is None or goal.source != 'AUTOPILOT' or goal.cancelled \
                or not isinstance(step.action, Zone) or step.action.zone_type != 'stockpile' or progress.state != 'complete':
            continue
        receipt = progress.issued.get('0', {})
        if receipt.get('confirmed') is not True or not receipt.get('zone_id'):
            continue
        zones['stockpile:' + str(receipt['zone_id'])] = {(x, z) for p in step.action.patches
            for x in range(p.x, p.x + p.width) for z in range(p.z, p.z + p.height)}
    if not owned and not zones:
        return []
    raw = facts.get('upkeep') or {}
    coverage = raw.get('homeCoverage')
    if raw.get('tick') != facts.get('tick') or 'homeCoverage' in raw.get('errors', {}) \
            or not isinstance(coverage, dict) or type(coverage.get('revision')) is not int \
            or not isinstance(coverage.get('targets'), list):
        return None
    rows = coverage['targets']
    if any(not isinstance(r.get('id'), str) for r in rows) or len({r['id'] for r in rows}) != len(rows):
        return None
    result = []
    for row in rows:
        if row['id'] not in owned and row['id'] not in zones:
            continue
        if type(row.get('missing')) is not int or type(row.get('excluded')) is not int \
                or not isinstance(row.get('shape'), str) or not isinstance(row.get('cells'), list):
            return None
        cells = row['cells']
        if not 1 <= len(cells) <= 256 or any(not isinstance(c, dict) or type(c.get('x')) is not int
                or type(c.get('z')) is not int for c in cells) or len({(c['x'], c['z']) for c in cells}) != len(cells) \
                or not 0 <= row['excluded'] <= row['missing'] <= len(cells):
            return None
        if row['id'] in zones and {(c['x'], c['z']) for c in row['cells']} != zones[row['id']]:
            result.append(dict(row, blocker='Owned stockpile geometry changed; preserve player edits', count=row['missing']))
        elif row['missing']:
            result.append(dict(row, count=row['missing']))
    return sorted(result, key=lambda r: r['id'])


async def method(rt, facts):
    from .colony_skills import SkillBlocked
    rows = targets(rt.current_plan, facts)
    if rows is None:
        raise SkillBlocked('Native Home coverage or autonomous ownership unavailable')
    failures = []
    goal = rt.current_plan.colony_goals['MaintainHomeCoverage']
    for row in rows[:8]:
        if row.get('blocker') or row['excluded']:
            failures.append(row.get('blocker') or 'Player or pre-observation Home exclusions are preserved')
            continue
        key = 'home-' + fingerprint(dict(target=row['id'], shape=row['shape']))[:16]
        if goal.method_seen(key):
            failures.append('Previously issued Home coverage requires observation; no duplicate write')
            continue
        return key, [dict(kind='native_operation', tool='home/upkeep_home', arguments=dict(
            target=row['id'], shape=row['shape'], revision=facts['upkeep']['homeCoverage']['revision']))]
    if failures:
        raise SkillBlocked('; '.join(sorted(set(failures))))
    return None


async def guard(rt, step):
    if step.source != 'AUTOPILOT' or step.goal_id != 'MaintainHomeCoverage':
        raise ValueError('Home coverage requires autonomous facility ownership')
    facts = await rt.game.query('home/colony_facts', planning=True)
    rows = targets(rt.current_plan, facts)
    args = step.action.arguments
    row = next((r for r in rows or [] if r['id'] == args['target']), None)
    if row is None or row.get('blocker') or row['excluded'] or row['shape'] != args['shape'] \
            or facts['upkeep']['homeCoverage']['revision'] != args['revision']:
        raise ValueError('Native Home coverage, ownership or player area changed')
