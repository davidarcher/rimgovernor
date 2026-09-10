"""Build and expand powered cold storage using ordinary labor and explicit chat."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.colony_plan import Buildings, ColonyGoal, CommitSteps
from rimbot.campaign_manifest import capture_manifest
from rimbot.bridge import runtime_file_read
from rimbot.session_checkpoint import create_checkpoint, prepare_resume

SIZE=8


async def run(args):
    source=json.loads(args.resume_report.read_text()) if args.resume_report else None
    checkpoint=source['powered_checkpoint']['manifest_path'] if source else None
    if checkpoint:
        data,state=prepare_resume(checkpoint)
        args.output.mkdir(parents=True,exist_ok=False)
        root=Path(data['root']);configuration=root/'config-headless'
        store=Store(state/'bridge.sqlite')
    else:
        root=isolated_root(args.source_root,args.output/'bridge');configuration=prepare(root)
        store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,resume=checkpoint,settings=Settings(model=args.model,timeout_seconds=90))
    report={'outcome':'failed','cases':[],'samples':[],'scope':'Ordinary powered freezer capacity expansion with native temperature and explicit local-model refinements'}
    deadline=time.monotonic()+args.seconds
    protected=None
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def command(**payload):
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    async def chat(prompt):
        previous=rt.chat[-1]['id'] if rt.chat else 0
        await rt.steer(prompt);revision=rt.chat_revision
        async with asyncio.timeout(180):
            while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:
                await asyncio.sleep(.5)
        await rt.execute_manual_requests()
        messages=[m for m in rt.chat if m.get('id',0)>previous]
        report.setdefault('chat',[]).append({'prompt':prompt,'messages':messages})
        assert not any('could not be interpreted' in m.get('text','') for m in messages),messages
    async def buildings():
        value=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        assert value.get('success') and not value.get('skipped',{}).get('byMaxDetailed')
        return value
    async def room(bounds):
        value=await rt.game.query('home/list_rooms',x=bounds['x']+1,z=bounds['z']+1,cells=True)
        interior={(x,z) for x in range(bounds['x']+1,bounds['x']+SIZE-1) for z in range(bounds['z']+1,bounds['z']+SIZE-1)}
        return next((r for r in value.get('rooms',[]) if r.get('properRoom') is True
            and r.get('cellsComplete') is True and {(p['x'],p['z']) for p in r['cells']}==interior),None)
    async def window(label):
        assert time.monotonic()<deadline,'Bounded construction/temperature deadline expired'
        if rt.review_task and not rt.review_task.done():await rt.review_task
        await rt.supervisor.change('Superfast',max_ticks=600)
        async with asyncio.timeout(25):
            while True:
                clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                if not clock['active']:break
                await asyncio.sleep(.15)
        if clock['stopReason']=='letter_pause' and 'Ancient danger' in clock.get('stopDetail',''):
            status=await rt.game.query('home/status',colonists=False,threats=True)
            counts=status.get('counts',{})
            record('observed_warning_without_active_threat',counts.get('hostileCount')==0
                and counts.get('huntingPredatorCount')==0,clock=clock,status=status)
            rt.supervisor.absorb(clock);rt.supervisor.allow_resume()
        else:
            assert clock['stopReason'] in ('tick_budget','requested_pause') and clock['pauseVerified'],clock
        if rt.review_task and not rt.review_task.done():await rt.review_task
        observed=await buildings()
        sample={'phase':label,'tick':clock.get('lastTick'),'clock':clock,'buildings':observed}
        if protected:
            original=await room(protected)
            sample['protected_room']=original
            assert original and original['openRoofCount']==0 and original['temperature']<=0,'Original freezer lost usable cold storage during expansion'
        report['samples'].append(sample)
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(json.dumps({'phase':label,'tick':clock.get('lastTick'),'attention':observed.get('attention')}),flush=True)
        return observed
    async def compile_setup(identity):
        facts=await rt.game.query('home/colony_facts',planning=True)
        people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,health=True))['pawns']
        goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=2,source='PLAYER'))
        selected=await rt.controller.skills.compile(identity,facts,people)
        if not selected:return False
        method,actions=selected
        if not actions:return False
        steps,_=rt.controller.skills.steps(identity,method,actions,facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Ordinary construction fixture: '+identity,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
        return True
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root,configuration,rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        if source:
            report['resumed_from']=str(args.resume_report.resolve())
            report['site']=source['site']
            first,second=source['site']['first'],source['site']['second']
            x,z=first['x'],first['z']
        else:
            while await compile_setup('AllowStartingSupplies'):pass
            while await compile_setup('EnsureWorkAssignments'):pass
            facts=await rt.game.query('home/colony_facts',planning=True)
            report['starting_facts']=facts
            available={(c['x'],c['z']) for c in facts['cells'] if c.get('walkable') and not c.get('occupied')}
            origins=sorted(available,key=lambda p:(p[0]+SIZE-facts['center']['x'])**2+(p[1]+2-facts['center']['z'])**2)
            selected=None
            for x,z in origins:
                if not {(a,b) for a in range(x,x+SIZE*2+1) for b in range(z-3,z+SIZE)}<=available:continue
                preview=await rt.game.invoke('home/place_building',dict(defName='WoodFiredGenerator',x=x+SIZE,z=z-2,dryRun=True))
                if preview.get('canPlace') is True:
                    cooling=[]
                    for bx in (x,x+SIZE+1):
                        cooling.append(await rt.game.invoke('home/place_building',dict(defName='Cooler',x=bx+SIZE//2,z=z,rotation='south',dryRun=True)))
                    if all(result.get('canPlace') is True for result in cooling):
                        selected=(x,z);break
                    if len(report.setdefault('site_refusals',[]))<4:report['site_refusals'].append({'x':x,'z':z,'coolers':cooling})
                    if any(result.get('researchFinished') is False for result in cooling):break
            assert selected,'No native clear two-room freezer site and generator footprint found'
            x,z=selected
            first,second={'x':x,'z':z},{'x':x+SIZE+1,'z':z}
            report['site']={'first':first,'second':second,'size':SIZE,'generator':{'x':x+SIZE,'z':z-2}}
        def shell(bounds):
            bx,bz=bounds['x'],bounds['z'];placements=[]
            for a in range(bx,bx+SIZE):
                for b in range(bz,bz+SIZE):
                    if a not in (bx,bx+SIZE-1) and b not in (bz,bz+SIZE-1):continue
                    if (a,b)==(bx+SIZE//2,bz):placements.append(dict(def_name='Cooler',x=a,z=b,rotation='south'))
                    else:placements.append(dict(def_name='Door' if (a,b)==(bx+SIZE//2,bz+SIZE-1) else 'Wall',
                        x=a,z=b,materials=['WoodLog','Steel']))
            return {'kind':'place_buildings','placements':placements}
        if source:
            observed=await buildings();native_room=await room(first)
            cooler=next(b for b in observed['buildings'] if b['defName']=='Cooler'
                and b['position']=={'x':x+SIZE//2,'z':z})
            record('paired_powered_checkpoint_resumed',native_room and native_room['openRoofCount']==0
                and rt.batch.summary.end_tick>=source['powered_checkpoint']['tick'],room=native_room)
        else:
            original=await command(kind='PlaceBuildings',purpose='storage',buildings=shell(first))
            generator=await command(kind='PlaceBuildings',buildings={'kind':'place_buildings','placements':[
                dict(def_name='WoodFiredGenerator',x=x+SIZE,z=z-2)]})
            for _ in range(100):
                observed=await window('build_original')
                cooler=next((b for b in observed['buildings'] if b['defName']=='Cooler' and b['position']=={'x':x+SIZE//2,'z':z}),None)
                native_room=await room(first)
                if cooler and native_room and native_room['openRoofCount']==0 and cooler.get('power',{}).get('powered') is True:break
            else:raise AssertionError('Ordinary first freezer construction/power did not complete')
        record('first_room_completed_powered',cooler['thermalSides']['readable'] and cooler['power']['powered'],room=native_room,cooler=cooler)
        report['powered_checkpoint']=await create_checkpoint(rt,rt.context_token)
        await chat('Set the temperature of the exact cooler '+cooler['thingId']+' to minus 10 Celsius. Change no other buildings.')
        await command(kind='CreateZone',intent_id='original-freezer-stock',zone={'kind':'create_zone','zone_type':'stockpile',
            'label':'Original freezer','patches':[dict(x=x+1,z=z+1,width=SIZE-2,height=SIZE-2)],'preset':'food','priority':'Important'})
        for _ in range(60):
            await window('cool_original');native_room=await room(first)
            report['samples'][-1]['room']=native_room
            if native_room and native_room['temperature']<=0 and native_room['stockpileCellsInRoom']==(SIZE-2)**2:break
        else:raise AssertionError('Original powered freezer did not reach freezing')
        record('original_usable_cold_storage',native_room['temperature']<=0 and native_room['stockpileCellsInRoom']==(SIZE-2)**2,room=native_room)
        protected=first
        report['original_checkpoint']=await create_checkpoint(rt,rt.context_token)
        # Replenishment is ordinary tree designation/pawn labor, before expansion
        # reserves are imposed. No supplies or pawn skills are edited.
        facts=await rt.game.query('home/colony_facts',planning=True)
        if facts.get('resources',{}).get('WoodLog',0)<200:
            await compile_setup('MaintainWood')
            for _ in range(60):
                await window('replenish_wood')
                facts=await rt.game.query('home/colony_facts',planning=True)
                if facts.get('resources',{}).get('WoodLog',0)>=200:break
        await chat('Reserve 80 steel for later. Keep normal component spending. Make no construction changes yet.')
        record('local_model_refines_resource_policy',rt.current_plan.control.get('resource_policy',{}).get('Steel',{}).get('reserve')==80,
            policies=rt.current_plan.control.get('resource_policy'))
        before_ids={s.id for s in rt.current_plan.spec.steps}
        await chat('Expand our freezer capacity by building this second separate cold-storage room beside the existing working freezer. '
            'Keep the original room and its orders. Use PlaceBuildings with purpose storage and this exact inspected specification: '+json.dumps(shell(second))+'.')
        expansions=[s for s in rt.current_plan.spec.steps if s.id not in before_ids and s.action.kind=='place_buildings']
        record('local_model_selects_expansion_geometry',len(expansions)==1 and expansions[0].action.model_dump()==
            Buildings.model_validate(shell(second)).model_dump())
        for _ in range(100):
            observed=await window('build_expansion')
            expanded_cooler=next((b for b in observed['buildings'] if b['defName']=='Cooler' and b['position']=={'x':second['x']+SIZE//2,'z':z}),None)
            expanded_room=await room(second)
            if expanded_cooler and expanded_room and expanded_room['openRoofCount']==0 and expanded_cooler.get('power',{}).get('powered'):break
        else:raise AssertionError('Ordinary expanded freezer construction/power did not complete')
        await chat('Set exact cooler '+expanded_cooler['thingId']+' to minus 15 Celsius. Leave the original cooler alone.')
        zone={'kind':'create_zone','zone_type':'stockpile','label':'Expanded freezer','patches':[dict(x=second['x']+1,z=z+1,width=SIZE-2,height=SIZE-2)],'preset':'food','priority':'Important'}
        await chat('Create a food stockpile in the new freezer using intent_id expanded-freezer-stock and this exact inspected zone: '+json.dumps(zone)+'. Preserve the old stockpile.')
        stable=0
        for _ in range(60):
            observed=await window('verify_expanded_cooling');expanded_room=await room(second)
            if expanded_room and expanded_room['temperature']<=0 and expanded_room['stockpileCellsInRoom']==(SIZE-2)**2:stable+=1
            else:stable=0
            if stable>=3:break
        else:raise AssertionError('Expanded freezer did not maintain its freezing storage cells over three native windows')
        original_room=await room(first)
        record('expanded_usable_cold_storage_preserves_original',original_room['temperature']<=0
            and original_room['stockpileCellsInRoom']==(SIZE-2)**2 and expanded_room['temperature']<=0
            and expanded_room['stockpileCellsInRoom']==(SIZE-2)**2,original=original_room,expanded=expanded_room,buildings=observed)
        report['checkpoint']=await create_checkpoint(rt,rt.context_token)
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error)
        report['error_evidence']=getattr(error,'evidence',None)
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
    parser.add_argument('--source-root',type=Path)
    parser.add_argument('--resume-report',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default='qwen3.5-4b')
    parser.add_argument('--seconds',type=int,default=2400)
    args=parser.parse_args()
    if bool(args.source_root)==bool(args.resume_report):parser.error('Choose exactly one source root or powered checkpoint report')
    asyncio.run(asyncio.wait_for(run(args),args.seconds+240))
