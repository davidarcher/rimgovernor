"""Verify bounded native Home coverage and saved player exclusions."""
import argparse
import asyncio
from pathlib import Path

from storeroom_acceptance import run


async def verify(rt, report, *, after_restart=False):
    from rimgovernor.colony_plan import ColonyGoal, CommitSteps
    from rimgovernor.home_coverage import method, targets
    from rimgovernor.construction_ownership import owned_buildings
    facts = await rt.game.query('home/colony_facts', planning=True)
    goal_id = 'MaintainHomeCoverage'
    goal = rt.current_plan.colony_goals.setdefault(goal_id, ColonyGoal(priority_class=3))
    if after_restart:
        result = report['home_coverage']
        target = result['target']
        actual = (await rt.bridge.call('test/home_coverage_read', target=target)).structuredContent
        assert actual['excluded'] >= 1 and actual['covered'] < actual['total'], actual
        row = next(r for r in targets(rt.current_plan, facts) if r['id'] == target)
        assert row['excluded'] >= 1
        refused = await rt.game.invoke('home/upkeep_home', dict(target=target, shape=row['shape'],
            revision=facts['upkeep']['homeCoverage']['revision'], dryRun=True), allow_write=False)
        assert refused['accepted'] is False and 'exclusions' in refused['error'], refused
        result['after_restart'] = actual
        result['bulk'] = (await rt.bridge.call('test/home_bulk_edits', target=target)).structuredContent
        assert result['bulk']['success'], result['bulk']
        result['outcome'] = 'passed'
        print('Home coverage: bounded native cells and saved player exclusions verified', flush=True)
        return
    owned = sorted(owned_buildings(rt.current_plan, facts))
    zones = sorted(str(r['zone_id']) for p in rt.current_plan.progress.values() for r in p.issued.values()
                   if r.get('confirmed') is True and r.get('zone_id'))
    target = owned[0] if owned else 'stockpile:' + zones[0]
    result = report['home_coverage'] = dict(target=target)
    result['setup'] = (await rt.bridge.call('test/home_coverage_setup', target=target)).structuredContent
    assert result['setup']['success']
    # One native facility may share its room scope with neighboring owned walls.
    facts = await rt.game.query('home/colony_facts', planning=True)
    key, actions = await method(rt, facts)
    stale = actions[0]['arguments']
    reset = (await rt.bridge.call('test/home_coverage_setup', target=stale['target'])).structuredContent
    assert reset['success']
    result['stale_refusal'] = await rt.game.invoke('home/upkeep_home', dict(stale, dryRun=True), allow_write=False)
    assert result['stale_refusal']['accepted'] is False and 'changed' in result['stale_refusal']['error']
    facts = await rt.game.query('home/colony_facts', planning=True)
    key, actions = await method(rt, facts)
    result['target'] = target = actions[0]['arguments']['target']
    baseline = (await rt.bridge.call('test/home_coverage_read', target=target)).structuredContent
    steps, _ = rt.controller.skills.steps(goal_id, key, actions, facts)
    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
        reason='Cover exact autonomous facility', steps=steps).decision(rt.current_plan),
        actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
    goal.steps.extend(s.id for s in steps)
    goal.evidence.setdefault('methods', {})[key] = [s.id for s in steps]
    rt.execution_task = asyncio.current_task()
    rt.handled_revision, rt.mode = rt.chat_revision, 'automate'
    await rt.hands.advance(rt)
    rt.mode = 'manual'
    assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
    covered = (await rt.bridge.call('test/home_coverage_read', target=target)).structuredContent
    assert covered['covered'] == covered['total'] and covered['outsideHome'] == baseline['outsideHome'], covered
    result['covered'] = covered
    removed = (await rt.bridge.call('test/home_player_remove', target=target)).structuredContent
    assert removed['success'], removed
    result['removed'] = removed
    facts = await rt.game.query('home/colony_facts', planning=True)
    assert next(r for r in targets(rt.current_plan, facts) if r['id'] == target)['excluded'] >= 1
    from rimgovernor.colony_upkeep import upkeep_nodes
    upkeep_nodes(facts, rt.current_plan.control, rt.current_plan.colony_goals, plan=rt.current_plan)
    state = rt.current_plan.control['upkeep'][goal_id]
    assert state['known'] and state['active']
    result['progress'] = [rt.current_plan.progress[s.id].model_dump() for s in steps]
    result['coverage_outcome'] = 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=300)
    args = parser.parse_args()
    args.wall_upgrade = False
    args.home_coverage = True
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
