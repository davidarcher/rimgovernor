"""Real-model numbered work and bill orders followed by ordinary pawn cooking.

The disposable fixture enables numbered priorities and replaces one starting
pemmican stack with rice. Native actions build a campfire and allow supplies;
the model enables the cook and creates the bill. No instant production is used.
"""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
import subprocess
import time
import traceback
import xml.etree.ElementTree as ET
from pathlib import Path

from rimgovernor.bridge import bridge_session
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_observation import observe
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.colony_plan import CommitSteps
from rimgovernor.config import Settings, ModelRole
from rimgovernor.consultation import structured_tool
from rimgovernor.execution_contracts import ExecutionContracts
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.model import LocalModel
from rimgovernor.store import Store


def require(value, evidence):
    if not value:
        raise AssertionError(evidence)


def bill_at(payload, bench_id, recipe):
    benches = [b for b in payload['benches'] if b['thingId']==bench_id]
    require(len(benches)==1 and len(benches[0]['bills'])==1, payload)
    bill = benches[0]['bills'][0]
    require(bill['index']==0 and bill['recipe']==recipe and
            bill['config']['repeatMode']=='RepeatCount', bill)
    return bill


def units(payload, definition):
    return sum(t['total'] for t in payload['things'] if t['defName']==definition)


def fixture(root):
    path = root / 'profile/Saves/RimGovernor-tribal8-baseline.rws'
    original = path.read_bytes()
    tree = ET.fromstring(original.decode('utf-8'))
    settings = tree.find('.//playSettings')
    manual = settings.find('useWorkPriorities')
    if manual is None:
        manual = ET.SubElement(settings, 'useWorkPriorities')
    manual.text = 'True'
    rice = next(t for t in tree.findall('.//maps/li/things/thing') if t.findtext('def') == 'Pemmican')
    rice.find('def').text = 'RawRice'
    rice.find('id').text = rice.findtext('id').replace('Pemmican', 'RawRice')
    rice.find('stackCount').text = '75'
    ET.ElementTree(tree).write(path, encoding='utf-8', xml_declaration=True)
    return dict(source_sha256=hashlib.sha256(original).hexdigest(),
                fixture_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                changes=['useWorkPriorities=True', 'One 75-unit pemmican stack replaced by RawRice'],
                rice_id=rice.findtext('id'), rice_position=rice.findtext('pos'))


async def commit_model(rt, model, tool, expected, instruction, report):
    schema = await rt.game.describe(tool)
    tools = [structured_tool('commit_steps', 'Commit the requested operation', CommitSteps.model_json_schema())]
    ExecutionContracts(tools).expose(tool, schema)
    trial = {'tool': tool, 'expected': expected, 'native_schema':schema,
             'commit_schema':tools[0]['function']['parameters']}
    report['model_trials'].append(trial)
    async def progress(_):
        pass
    messages = [{'role': 'user', 'content':
        f'Current revision {rt.current_plan.revision}. Commit exactly one native_operation '
        'using commit_steps. Native arguments MUST contain "dryRun":false; omission is refused. '
        'Disable decorative UI. Required native arguments: '
        + json.dumps(dict(expected, dryRun=False)) + '. ' + instruction}]
    trial['attempts'] = []
    for attempt in range(3):
        answer, usage = await model.complete(messages, tools, True, progress)
        record = dict(answer=answer, usage=usage)
        trial['attempts'].append(record)
        calls = answer.get('tool_calls', [])
        require(len(calls) == 1 and calls[0]['function']['name'] == 'commit_steps', answer)
        try:
            proposal = CommitSteps.model_validate_json(calls[0]['function']['arguments'])
            require(len(proposal.steps) == 1, proposal.model_dump())
            action = proposal.steps[0].action
            require(action.kind == 'native_operation' and action.tool == tool, action.model_dump())
            differences = {k:{'expected':v,'received':action.arguments.get(k)}
                           for k,v in expected.items() if action.arguments.get(k) != v}
            require(not differences, {'correct_native_arguments':differences})
            require(action.arguments.get('dryRun') is False, 'Include dryRun:false in native arguments')
            for key, value in action.arguments.items():
                if key not in expected and key != 'dryRun':
                    require(value in (None, '') or value == schema['properties'][key].get('default') or
                            (key in ('watch','keepSelected') and value is False) or
                            (key=='repeatMode' and value=='RepeatCount'), {key:value})
            break
        except (ValueError, AssertionError) as error:
            record['rejected'] = str(error)
            if attempt == 2:
                raise
            messages.extend([answer, {'role':'tool','tool_call_id':calls[0]['id'],
                'content':json.dumps({'error':str(error), 'no_action_executed':True})}])
    await rt.commit_strategy(proposal.decision(rt.current_plan), actor=ModelRole.STRATEGIST,
                            expected_token=rt.context_token, expected_revision=rt.chat_revision)
    await rt.hands.advance(rt)
    trial['progress'] = rt.current_plan.progress[proposal.steps[0].id].model_dump()
    require(trial['progress']['state'] == 'complete', trial['progress'])
    print(json.dumps({'phase':'model_order_completed','tool':tool}),flush=True)


async def run(args):
    root = isolated_root(args.source_root, args.output)
    report = dict(outcome='error', model=args.model, model_trials=[], history=[],
                  revision=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip())
    report['fixture'] = fixture(root)
    start = time.monotonic()
    try:
        async with bridge_session(gabs_executable(root), prepare(root)) as bridge:
            await bridge.core('games_start', gameId=bridge.game_id)
            await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                              readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            store = Store(root/'state.sqlite')
            rt = BridgeRuntime(store, root)
            rt.bridge, rt.game = bridge, BridgeGame(bridge)
            model = LocalModel(Settings(model=args.model))
            try:
                await rt.sync_identity()
                rt.batch = await observe(rt.game)
                rt.mode = 'automate'
                people = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
                report['people_before'] = people
                cook = next(p for p in people['pawns'] if any(w['name']=='Cooking' and not w['disabled'] for w in p['work']['types']))
                cook_id = cook['thingId']
                require(cook['work']['manualPriorities'] is True, cook)
                for p in people['pawns']:
                    changes = {'pawn':p['thingId'], 'work':'Cooking=0', 'schedule':'W'*24, 'dryRun':False, 'watch':False}
                    await rt.game.invoke('home/pawn_config', changes, allow_write=True)
                orders = await rt.game.invoke('rimworld/list_architect_designators', {'categoryId':'Orders'})
                allow = next(d for d in orders['designators'] if d['className']=='RimWorld.Designator_Unforbid')
                for definition in ('RawRice', 'WoodLog', 'Pemmican'):
                    supplies = await rt.game.query('home/list_things', match=definition, includeHeld=False, maxPositionsPerDef=100)
                    for row in supplies['things']:
                        if row['defName'] == definition:
                            for pos in row['positions']:
                                await rt.game.invoke('rimworld/apply_architect_designator',
                                    {'designatorId':allow['id'], 'x':pos['x'], 'z':pos['z'], 'dryRun':False, 'keepSelected':False}, allow_write=True)
                origin = cook['position']
                for dx in range(3, 12):
                    placement = dict(defName='Campfire', x=origin['x']+dx, z=origin['z'], rotation='north', dryRun=True)
                    preview = await rt.game.invoke('home/place_building', placement)
                    if preview.get('canPlace'):
                        report['campfire_order'] = await rt.game.invoke('home/place_building', dict(placement,dryRun=False), allow_write=True)
                        break
                else:
                    raise AssertionError('No legal campfire fixture site')
                await rt.game.invoke('rimworld/set_time_speed', {'speed':'Fast','ultraSpeedBoost':False}, allow_write=True)
                deadline = time.monotonic()+180
                while time.monotonic()<deadline:
                    await asyncio.sleep(1)
                    buildings = await rt.game.query('home/list_buildings', match='Campfire', aggregate=False, playerOnly=True)
                    ready = [b for b in buildings['buildings'] if b['status']=='built'
                             and b['position']=={'x':placement['x'],'z':placement['z']}]
                    if ready:
                        bench = ready[0]
                        break
                else:
                    raise AssertionError({'campfire_not_built':buildings})
                await rt.game.invoke('rimworld/set_time_speed', {'speed':'Paused','ultraSpeedBoost':False}, allow_write=True)
                report['built_campfire'] = bench
                recipes = await rt.game.invoke('home/bills', {'action':'recipes','bench':bench['thingId'],'dryRun':True})
                report['recipes'] = recipes
                recipe = next(r for r in recipes['recipes'] if r['defName']=='CookMealSimple' and r['availableNow'])
                await commit_model(rt, model, 'home/pawn_config', {'pawn':cook_id,'work':'Cooking=1'},
                    f'Enable Cooking at numbered priority 1 for {cook_id}; change no other setting. '
                    f'Observed work: {json.dumps(cook["work"])}', report)
                readback = await rt.game.invoke('home/pawn_config', {'pawn':cook_id,'dryRun':True})
                report['numbered_work_readback'] = readback
                work = readback['after']['work']
                require(work['manualPriorities'] is True, work)
                priority = next(w for w in work['types'] if w['name']=='Cooking')
                require(priority['priority']==priority['priorityStored']==1, priority)
                await commit_model(rt, model, 'home/bills', {'action':'add','bench':bench['thingId'],'recipe':recipe['defName'],'repeatCount':2},
                    f'Add one Do 2 times bill for recipe {recipe["defName"]} on {bench["thingId"]}. '
                    f'Observed recipe: {json.dumps(recipe)}. Do not claim production.', report)
                report['bills_before'] = await rt.game.invoke('home/bills', {'action':'list','bench':bench['thingId'],'dryRun':True})
                initial_bill = bill_at(report['bills_before'],bench['thingId'],recipe['defName'])
                require(initial_bill['active'] and initial_bill['config']['repeatCount']==2, initial_bill)
                report['rice_before'] = await rt.game.query('home/list_things', match='RawRice', includeHeld=True, maxPositionsPerDef=100)
                report['meals_before'] = await rt.game.query('home/list_things', match='MealSimple', includeHeld=True, maxPositionsPerDef=100)
                require(not any(t['defName']=='MealSimple' and t['total'] for t in report['meals_before']['things']), 'Fixture already has meals')
                await rt.game.invoke('rimworld/set_time_speed', {'speed':'Normal','ultraSpeedBoost':False}, allow_write=True)
                deadline = time.monotonic()+args.seconds
                while time.monotonic()<deadline:
                    await asyncio.sleep(.25)
                    pawns = await rt.game.query('home/list_pawns', colonistsOnly=True)
                    worker = next(p for p in pawns['pawns'] if p['thingId']==cook_id)
                    bills = await rt.game.invoke('home/bills', {'action':'list','bench':bench['thingId'],'dryRun':True})
                    meals = await rt.game.query('home/list_things', match='MealSimple', includeHeld=True, maxPositionsPerDef=100)
                    row = dict(elapsed=round(time.monotonic()-start,2), worker=worker, bills=bills, meals=meals)
                    report['history'].append(row)
                    actual = bill_at(bills,bench['thingId'],recipe['defName'])
                    if actual['finished']:
                        break
                    if len(report['history'])%20==0:
                        print(json.dumps({'phase':'cooking','job':worker['job'],'carrying':worker.get('carriedThingId')}),flush=True)
                await rt.game.invoke('rimworld/set_time_speed', {'speed':'Paused','ultraSpeedBoost':False}, allow_write=True)
                report['rice_after'] = await rt.game.query('home/list_things', match='RawRice', includeHeld=True, maxPositionsPerDef=100)
                require(actual['finished'] and actual['config']['repeatCount']==0, {'unfinished_bill':actual})
                require(any(r['worker']['job']=='DoBill' and 'RawRice' in
                    (r['worker'].get('carriedThingId') or '') for r in report['history']),
                    'Inconclusive: ingredient transport during DoBill was not observed')
                require(units(report['rice_after'],'RawRice') < units(report['rice_before'],'RawRice'), 'No ingredient decrement observed')
                require(any(units(r['meals'],'MealSimple')>0 for r in report['history']), 'Inconclusive: produced meals not observed')
                report['outcome']='passed'
            finally:
                try:
                    await rt.halt()
                finally:
                    report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
                    await model.close()
                    await rt.router.close()
                    store.close()
                    await bridge.core('games_stop',gameId=bridge.game_id)
    except Exception as error:
        report.update(outcome='error', error=str(error), traceback=traceback.format_exc())
    finally:
        report['elapsed_seconds']=round(time.monotonic()-start,2)
        (root/'result.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({k:report[k] for k in ('outcome','elapsed_seconds')} | {'error':report.get('error'),'evidence':str(root/'result.json')}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default='qwen3.5-9b')
    parser.add_argument('--seconds',type=int,default=240)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
