"""Furnish an edited native room and verify cold/hot recovery through ordinary labor."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.session_checkpoint import prepare_resume,create_checkpoint
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.colony_plan import ColonyGoal,CommitSteps,PlanStep
from rimbot.shelter_handoff import furniture_handoff,player_shelter
from rimbot.campaign_manifest import capture_manifest
from rimbot.bridge import runtime_file_read


async def run(args):
    source=json.loads(args.freezer_report.read_text())
    assert source['outcome']=='passed'
    checkpoint=source['checkpoint']['manifest_path']
    data,state=prepare_resume(checkpoint)
    args.output.mkdir(parents=True,exist_ok=False)
    store=Store(state/'bridge.sqlite')
    rt=BridgeRuntime(store,Path(data['root']),fresh=True,headless=True,resume=checkpoint,
        settings=Settings(model=args.model,timeout_seconds=90))
    report={'outcome':'failed','variant':args.variant,'source_checkpoint':checkpoint,'cases':[],'samples':[]}
    bounds=dict(source['site']['second'],width=source['site']['size'],height=source['site']['size'])
    deadline=time.monotonic()+args.seconds
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def settle():
        rt.clock_events.extend(await rt.supervisor.poll())
        rt.receive_clock_events()
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):
                await asyncio.sleep(.1)
    async def command(**payload):
        await settle()
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        if payload['kind']=='PlaceBuildings':
            progress=rt.current_plan.progress[result['step']]
            assert len(progress.issued)==len(payload['buildings']['placements']),progress.model_dump()
        return result
    async def chat(prompt):
        await settle()
        await rt.steer(prompt);revision=rt.chat_revision
        async with asyncio.timeout(180):
            while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:
                await asyncio.sleep(.5)
        await rt.execute_manual_requests()
        report.setdefault('chat',[]).append({'prompt':prompt,'messages':[m for m in rt.chat if m.get('revision')==revision]})
    async def observations():
        rooms=await rt.game.query('home/list_rooms',x=bounds['x']+1,z=bounds['z']+1,cells=True)
        interior={(x,z) for x in range(bounds['x']+1,bounds['x']+bounds['width']-1)
                  for z in range(bounds['z']+1,bounds['z']+bounds['height']-1)}
        room=next((r for r in rooms.get('rooms',[]) if r.get('cellsComplete') is True
            and {(p['x'],p['z']) for p in r['cells']}==interior),None)
        buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        assert buildings.get('success') and not buildings.get('skipped',{}).get('byMaxDetailed')
        return room,buildings
    async def window(label):
        assert time.monotonic()<deadline,'Bounded adopted-room acceptance deadline expired'
        if rt.review_task and not rt.review_task.done():await rt.review_task
        await rt.supervisor.change('Superfast',max_ticks=600)
        async with asyncio.timeout(25):
            while True:
                clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                if not clock['active']:break
                await asyncio.sleep(.15)
        assert clock['stopReason'] in ('tick_budget','requested_pause') and clock['pauseVerified'],clock
        await settle()
        room,buildings=await observations()
        report['samples'].append({'phase':label,'clock':clock,'room':room,'buildings':buildings})
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(json.dumps({'phase':label,'tick':clock.get('lastTick'),'temperature':room.get('temperature') if room else None}),flush=True)
        return room,buildings
    async def commit_actions(identity,actions):
        await settle()
        steps=[PlanStep(id=identity+'-'+str(index),title=identity,source='PLAYER',action=action,
            completion_criteria='Native fixture action completed') for index,action in enumerate(actions)]
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Explicit adopted-room acceptance: '+identity,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        return steps
    async def compile_goal(identity):
        await settle()
        facts=await rt.game.query('home/colony_facts',planning=True)
        people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,health=True))['pawns']
        goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=2,source='PLAYER'))
        selected=await rt.controller.skills.compile(identity,facts,people)
        assert selected,identity+' did not produce its expected native method'
        method,actions=selected
        steps,_=rt.controller.skills.steps(identity,method,actions,facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Production handoff method acceptance: '+identity,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
        return facts,actions,steps
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            Path(data['root']),Path(data['root'])/'config-headless',rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        room,buildings=await observations()
        cooler=next(b for b in buildings['buildings'] if b['defName']=='Cooler'
            and b['position']=={'x':bounds['x']+bounds['width']//2,'z':bounds['z']})
        # A real player-style furniture edit occupies the first fitting candidate.
        obstruction=await command(kind='PlaceBuildings',purpose='shelter',buildings={'kind':'place_buildings','placements':[
            dict(def_name='SleepingSpot',x=bounds['x']+1,z=bounds['z']+1)]})
        room,buildings=await observations()
        existing_beds={b['thingId'] for b in buildings['buildings'] if b['defName']=='SleepingSpot'
            and b['position']=={'x':bounds['x']+1,'z':bounds['z']+1}}
        record('ordinary_player_edit_exists',rt.current_plan.progress[obstruction['step']].state=='complete' and len(existing_beds)>=1)
        await chat('Use this existing enclosed room as our preferred shelter; preserve its existing furniture and construction. '
            'Use AdoptRoom with intent_id edited-thermal-home, bounds '+json.dumps(bounds)+' and entrance north. Do not build another shell.')
        selection=player_shelter(rt.current_plan)
        record('local_model_adopts_exact_edited_room',selection and selection[0]=='intent-edited-thermal-home'
            and selection[1].evidence['adoption']['room_id']==room['id'])
        facts,actions,steps=await compile_goal('EnsureInitialShelter')
        room,buildings=await observations()
        record('native_sleeping_handoff_preserves_player_edit',len(room['beds'])>=facts['colonists']
            and existing_beds<={b['thingId'] for b in buildings['buildings']}
            and all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            and not any(s.action.kind=='build_room_shell' for s in steps),room=room,actions=actions)
        await chat('Set exact cooler '+cooler['thingId']+' to 50 Celsius so this room can be used as shelter. Leave the original freezer cooler alone.')
        if args.variant=='hot':
            heater_actions=await furniture_handoff(rt,selection,'Heater')
            assert heater_actions
            await commit_actions('ordinary-fixture-heater',heater_actions)
            target=heater_actions[0]['placements'][0]
            for _ in range(80):
                room,buildings=await window('build_heater')
                heater=next((b for b in buildings['buildings'] if b['defName']=='Heater'
                    and b['position']=={'x':target['x'],'z':target['z']} and b.get('power',{}).get('powered')),None)
                if heater:break
            else:raise AssertionError('Ordinary heater construction/power did not complete')
            await command(kind='SetBuildingTemperature',thing=heater['thingId'],celsius=45)
            for _ in range(80):
                room,buildings=await window('ordinary_heat_variant')
                if room['temperature']>32:break
            else:raise AssertionError('Native heater did not establish hot-room variant')
            await command(kind='SetBuildingTemperature',thing=heater['thingId'],celsius=12)
        else:
            room,buildings=await observations()
            assert room['temperature']<12,'Native cold-room variant must still be cold before recovery'
        before=room['temperature']
        facts,actions,steps=await compile_goal('EnsureTemperatureSafety')
        expected='Campfire' if args.variant=='cold' else 'PassiveCooler'
        placements=[p for action in actions for p in action.get('placements',[])]
        record('correct_native_thermal_method',len(placements)==1 and placements[0]['def_name']==expected,
            observed_temperature=before,actions=actions,facts=facts)
        target=placements[0]
        for _ in range(100):
            room,buildings=await window('thermal_recovery')
            furniture=next((b for b in buildings['buildings'] if b['defName']==expected
                and b['position']=={'x':target['x'],'z':target['z']} and b.get('fuel',{}).get('hasFuel')),None)
            if furniture and (room['temperature']>=16 if args.variant=='cold' else room['temperature']<=28):break
        else:raise AssertionError('Native fueled thermal furniture did not reach recovery threshold')
        record('ordinary_thermal_recovery_completed',furniture and room['openRoofCount']==0
            and len(room['beds'])>=facts['colonists'],room=room,furniture=furniture)
        # Invalid current geometry must stop further placement even though the
        # original adoption remains in durable intent history.
        bad=dict(bounds,width=bounds['width']+1)
        before_plan=rt.current_plan.model_dump()
        refused=False
        try:await command(kind='AdoptRoom',intent_id='edited-thermal-home',bounds=bad,entrance='north')
        except ValueError:refused=True
        record('invalid_refinement_preserves_current_adoption',refused and rt.current_plan.model_dump()==before_plan)
        report['checkpoint']=await create_checkpoint(rt,rt.context_token)
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        report['plan']=rt.current_plan.model_dump()
        try:
            if rt.bridge:
                report['cleanup']= (await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)).structuredContent
        except Exception as cleanup_error:
            report['cleanup_error']=repr(cleanup_error)
        finally:
            await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--freezer-report',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--variant',choices=['hot','cold'],required=True)
    parser.add_argument('--model',default='qwen3.5-4b')
    parser.add_argument('--seconds',type=int,default=1500)
    args=parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args),args.seconds+240))
