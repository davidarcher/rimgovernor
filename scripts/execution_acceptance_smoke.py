"""Targeted real-model supply, work and bill acceptance on fresh paused games.

Each case reloads a paused eight-tribal baseline and uses a fresh controller database. Native readback, not a
write receipt, decides success. This does not test autonomous strategy or labor.
"""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from rimbot.bridge_observation import observe
from rimbot.colony_plan import CommitSteps
from rimbot.config import Settings, ModelRole
from rimbot.consultation import structured_tool
from rimbot.execution_contracts import ExecutionContracts
from rimbot.model import LocalModel
from rimbot.native_trials import ReusableGame
from rimbot.campaign_manifest import tracked_source
from rimbot.native_contracts import validate_arguments


def require(condition, evidence):
    if not condition:
        raise AssertionError(evidence)


async def setup(game, batch, case, report):
    pawn = batch.summary.pawns[0]
    if case == 'supplies':
        query = dict(match='Pemmican', includeHeld=False, x=pawn.position.x,
                     z=pawn.position.z, radius=30, maxPositionsPerDef=100)
        before = await game.query('home/list_things', **query)
        food = next(r for r in before['things'] if r['defName'] == 'Pemmican')
        require(food['oursUnforbidden'] == 0 and food['positionsNotListed'] == 0, food)
        stack = food['positions'][0]
        orders = await game.invoke('rimworld/list_architect_designators', {'categoryId': 'Orders'})
        allow = next(d for d in orders['designators'] if d['className'] == 'RimWorld.Designator_Unforbid')
        expected = dict(designatorId=allow['id'], x=stack['x'], z=stack['z'])
        report['before'] = before
        report['target'] = stack

        async def verify():
            after = await game.query('home/list_things', **query)
            report['after'] = after
            actual = next(r for r in after['things'] if r['defName'] == 'Pemmican')
            require(actual['ours'] == food['ours'], actual)
            require(actual['oursUnforbidden'] == stack['stackCount'], actual)
            require(actual['forbidden'] == food['forbidden'] - stack['stackCount'], actual)
            # A filtered native read must no longer contain the selected stack.
            forbidden = await game.query('home/list_things', **query, forbiddenOnly=True)
            report['forbidden_after'] = forbidden
            remaining = next(r for r in forbidden['things'] if r['defName'] == 'Pemmican')
            require(remaining['positionsNotListed'] == 0, remaining)
            require({p['thingId'] for p in remaining['positions']} ==
                    {p['thingId'] for p in food['positions']} - {stack['thingId']}, remaining)

        return ('rimworld/apply_architect_designator', expected,
                f'Allow only the starting pemmican at ({stack["x"]},{stack["z"]}) using this '
                f'observed Allow designator: {json.dumps(allow)}. Do not allow a rectangle or the whole map.', verify)

    if case == 'work':
        before = await game.invoke('home/pawn_config', {'pawn': pawn.thing_id, 'dryRun': True})
        work = before['after']['work']
        selected = next(w for w in work['types'] if w['name'] == 'Cooking' and
                        w['disabled'] is False and w['priority'] == 0)
        expected = dict(pawn=pawn.thing_id, work=selected['name'] + '=3')
        report['before'] = before
        report['acceptance_boundary'] = 'Enable work at priority 3; does not certify numbered priority ordering or actual work.'

        async def verify():
            after = await game.invoke('home/pawn_config', {'pawn': pawn.thing_id, 'dryRun': True})
            report['after'] = after
            actual = after['after']['work']
            require(actual['manualPriorities'] == work['manualPriorities'], actual)
            for row in actual['types']:
                old = next(w for w in work['types'] if w['name'] == row['name'])
                want = 3 if row['name'] == selected['name'] else old['priority']
                require(row['priority'] == want, row)
                require(row['priorityStored'] == (3 if row['name'] == selected['name'] else old['priorityStored']), row)
            for key in ('settings', 'schedule', 'animals'):
                require(after['after'][key] == before['after'][key], after['after'][key])

        return ('home/pawn_config', expected,
                f'Enable Cooking for pawn {pawn.thing_id} at priority 3. Change no other setting. '
                f'Observed work settings: {json.dumps(work)}', verify)

    # Butcher spots cost no materials and are normally placed instantly. This is
    # scripted fixture preparation, separately recorded from the model's bill.
    report['fixture_placement_previews'] = []
    for dx in range(3, 9):
        placement = dict(defName='ButcherSpot', x=pawn.position.x + dx,
                         z=pawn.position.z, rotation='north', dryRun=True)
        preview = await game.invoke('home/place_building', placement)
        report['fixture_placement_previews'].append(preview)
        if preview.get('canPlace'):
            report['fixture_placement'] = await game.invoke('home/place_building',
                dict(placement, dryRun=False), allow_write=True)
            break
    else:
        raise AssertionError('No legal nearby butcher spot')
    before = await game.invoke('home/bills', {'action': 'list', 'dryRun': True})
    report['before'] = before
    bench = next(b for b in before['benches'] if b['defName'] == 'ButcherSpot')
    require(bench['billCount'] == 0 and bench['usableForBills'], bench)
    recipes = await game.invoke('home/bills', {'action': 'recipes', 'bench': bench['thingId'], 'dryRun': True})
    report['recipes'] = recipes
    recipe = next(r for r in recipes['recipes'] if r['availableNow'])
    expected = dict(action='add', bench=bench['thingId'], recipe=recipe['defName'],
                    repeatCount=1)

    async def verify():
        after = await game.invoke('home/bills', {'action': 'list', 'bench': bench['thingId'], 'dryRun': True})
        report['after'] = after
        actual = next(b for b in after['benches'] if b['thingId'] == bench['thingId'])
        require(actual['billCount'] == 1, actual)
        bill = actual['bills'][0]
        require(bill['recipe'] == recipe['defName'] and bill['active'], bill)
        require(bill['config']['repeatMode'] == 'RepeatCount' and bill['config']['repeatCount'] == 1, bill)

    return ('home/bills', expected,
            f'Add exactly one bill to bench {bench["thingId"]} for recipe {recipe["defName"]}, '
            f'Do 1 time (RepeatCount). Observed recipe: {json.dumps(recipe)}. '
            'Issue the bill even if ingredients are unavailable; do not claim production.', verify)


async def exercise(args, case, rt, report):
    model = LocalModel(Settings(model=args.model))
    try:
        rt.batch = await observe(rt.game)
        rt.mode = 'automate'
        tool, expected, instruction, verify = await setup(rt.game, rt.batch, case, report)
        rt.batch = await observe(rt.game)
        tools = [structured_tool('commit_steps', 'Commit the requested change', CommitSteps.model_json_schema())]
        schema = await rt.game.describe(tool)
        ExecutionContracts(tools).expose(tool, schema)

        async def progress(_):
            pass

        report['model_requests'] += 1
        answer, usage = await model.complete([{'role': 'user', 'content':
            f'Paused disposable execution test. Current revision {rt.current_plan.revision}. '
            'Commit exactly one native operation with commit_steps and the supplied schema. '
            'Execute the change (dryRun false), disable decorative UI, leave all other settings unchanged. '
            + instruction}], tools, True, progress)
        report.update(answer=answer, usage=usage)
        calls = answer.get('tool_calls', [])
        require(len(calls) == 1 and calls[0]['function']['name'] == 'commit_steps', answer)
        proposal_args = json.loads(calls[0]['function']['arguments'])
        report['proposal'] = proposal_args
        validate_arguments('commit_steps', tools[0]['function']['parameters'], proposal_args)
        proposal = CommitSteps.model_validate(proposal_args)
        require(len(proposal.steps) == 1, proposal_args)
        action = proposal.steps[0].action
        require(action.kind == 'native_operation' and action.tool == tool, proposal_args)
        for key, value in expected.items():
            require(action.arguments.get(key) == value, action.arguments)
        for key, value in action.arguments.items():
            if key not in expected:
                default = schema['properties'][key].get('default')
                require(value == default or value is None or value == '' or
                        (key == 'repeatMode' and value == 'RepeatCount' and case == 'bill') or
                        (key in ('dryRun', 'watch', 'keepSelected') and value is False), action.arguments)
        await rt.commit_strategy(proposal.decision(rt.current_plan), actor=ModelRole.STRATEGIST,
            expected_token=rt.context_token, expected_revision=rt.chat_revision)
        await rt.hands.advance(rt)
        report['progress'] = rt.current_plan.progress[proposal.steps[0].id].model_dump()
        require(report['progress']['state'] == 'complete', report['progress'])
        await verify()
        status = await rt.game.query('home/status', colonists=False, threats=False)
        report['time'] = status['time']
        require(status['time']['paused'], status)
        report['outcome'] = 'passed'
    finally:
        await model.close()


async def run_case(args, case, game):
    report = dict(case=case, model=args.model, outcome='error', model_requests=0,
                  process_mode='reused' if args.reuse_game else 'fresh',
                  revision=tracked_source(Path(__file__).resolve().parents[1])['revision'])
    began = time.monotonic()
    try:
        async with game.trial(case, settings=Settings(model=args.model)) as rt:
            try:
                await exercise(args, case, rt, report)
            finally:
                report['events'] = rt.store.history(rt.colony, limit=10000, include_diagnostics=True)
    except Exception as error:
        report.update(outcome='error', error=f'{type(error).__name__}: {error}', traceback=traceback.format_exc())
    finally:
        report['elapsed_seconds'] = round(time.monotonic()-began, 2)
        (args.output/(case+'.json')).write_text(json.dumps(report, indent=2), encoding='utf8')
    result = {k: report[k] for k in ('case', 'outcome', 'model_requests', 'elapsed_seconds', 'process_mode')}
    result['evidence'] = str(args.output/(case+'.json'))
    if 'error' in report:
        result['error'] = report['error']
    print(json.dumps(result), flush=True)
    return result


async def main(args):
    args.output.mkdir(parents=True, exist_ok=False)
    cases = ('supplies', 'work', 'bill') if args.case == 'all' else (args.case,)
    results = []
    def save():
        (args.output/'summary.json').write_text(json.dumps(results, indent=2), encoding='utf8')
    if args.reuse_game:
        async with ReusableGame(args.source_root, args.output/'worker') as game:
            for index, case in enumerate(cases):
                results.append(await run_case(args, case, game))
                save()
                if results[-1]['outcome'] != 'passed':
                    results.extend(dict(case=name, outcome='not_run', reason='Reused worker retired after failure')
                                   for name in cases[index+1:])
                    save()
                    break
    else:
        for case in cases:
            async with ReusableGame(args.source_root, args.output/('worker-'+case)) as game:
                results.append(await run_case(args, case, game))
                save()
    return all(r['outcome'] == 'passed' for r in results)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--case', choices=('all', 'supplies', 'work', 'bill'), default='all')
    parser.add_argument('--reuse-game', action='store_true', help='Reload the baseline between cases in one owned process; stop on failure')
    parser.add_argument('--source-root', type=Path, default=Path('.rimbot/bridge'))
    parser.add_argument('--model', default='qwen3.5-9b')
    parser.add_argument('--output', type=Path, default=Path('.rimbot') / f'execution-acceptance-{time.time_ns()}')
    raise SystemExit(0 if asyncio.run(main(parser.parse_args())) else 1)
