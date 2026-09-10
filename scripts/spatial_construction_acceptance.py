"""Ordinary pawn construction and exact nonrectangular shelter reuse in a private game."""
import argparse
import asyncio
import json
import time
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge import runtime_file_read
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.headless import isolated_root, prepare
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import create_checkpoint
from rimbot.shelter_handoff import sleeping_handoff, player_shelter, entrance_aisle
from rimbot.store import Store


async def run(args):
    args.output.mkdir(parents=True,exist_ok=False)
    root=isolated_root(args.source_root,args.output/'bridge');configuration=prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report=dict(outcome='failed',cases=[],samples=[],scope='Ordinary L-shaped room construction, roof, exact adoption and furnishing; no long-term survival claim')
    started=False;deadline=time.monotonic()+args.seconds
    def save():
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence));save()
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def settle():
        rt.clock_events.extend(await rt.supervisor.poll());rt.receive_clock_events()
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):
                await asyncio.sleep(.1)
    async def command(payload):
        await settle()
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    async def window():
        assert time.monotonic()<deadline,'Native construction deadline expired'
        await settle()
        await rt.supervisor.change('Superfast',max_ticks=600)
        async with asyncio.timeout(30):
            while True:
                clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                if not clock['active']:break
                await asyncio.sleep(.2)
        if clock['stopReason']=='letter_pause' and 'Ancient danger' in clock.get('stopDetail',''):
            letters=await rt.game.invoke('rimworld/list_letters',{})
            threats=await rt.game.query('home/status',colonists=False,threats=True)
            counts=threats.get('counts',{})
            record('ancient_danger_warning_inspected',clock['pauseVerified'] and counts.get('hostileCount')==0
                and counts.get('huntingPredatorCount')==0,clock=clock,letters=letters,threats=threats)
            rt.supervisor.absorb(clock);rt.supervisor.allow_resume()
        else:
            assert clock['stopReason'] in ('tick_budget','requested_pause') and clock['pauseVerified'],clock
        await settle()
        return clock
    async def compile_goal(identity):
        await settle()
        facts=await rt.game.query('home/colony_facts',planning=True)
        people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,health=True))['pawns']
        goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=2,source='PLAYER'))
        selected=await rt.controller.skills.compile(identity,facts,people)
        if selected is None:return
        method,actions=selected
        steps,_=rt.controller.skills.steps(identity,method,actions,facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Spatial native acceptance '+identity,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,configuration,
            rt.router.routing.model_dump(mode='json'))
        started=True;await ready(rt);await settle()
        await compile_goal('AllowStartingSupplies');await compile_goal('EnsureWorkAssignments')
        facts=await rt.game.query('home/colony_facts',planning=True)
        layout=await rt.controller.skills.layout(facts);b=layout['room'];x,z=b['x'],b['z']
        interior={(a,c) for a in range(x+1,x+8) for c in range(z+1,z+8) if a<x+5 or c<z+5}
        boundary={(a+dx,c+dz) for a,c in interior for dx in (-1,0,1) for dz in (-1,0,1)}-interior
        door=(x+4,z)
        placements=[dict(def_name='Door' if p==door else 'Wall',x=p[0],z=p[1],materials=['WoodLog'])
            for p in sorted(boundary,key=lambda p:(p!=door,p))]
        required=0
        for placement in placements:
            preview=await rt.inspect_native('home/place_building',dict(defName=placement['def_name'],
                x=placement['x'],z=placement['z'],stuff='WoodLog',rotation='north',dryRun=True))
            assert preview.get('canPlace') is True and isinstance(preview.get('costList'),list),preview
            required+=sum(row['count'] for row in preview['costList'] if row['defName']=='WoodLog')
        while facts.get('resources',{}).get('WoodLog',0)<required:
            await compile_goal('MaintainWood')
            clock=await window()
            facts=await rt.game.query('home/colony_facts',planning=True)
            report['samples'].append(dict(phase='wood',clock=clock,wood=facts.get('resources',{}).get('WoodLog'),required=required))
            save();print(json.dumps(report['samples'][-1]),flush=True)
        built=await command(dict(kind='PlaceBuildings',purpose='shelter',buildings=dict(kind='place_buildings',placements=placements)))
        progress=rt.current_plan.progress[built['step']]
        record('ordinary_nonrectangular_orders_issued',len(progress.issued)==len(placements),
            bounds=b,interior=sorted(interior),placements=placements,progress=progress.model_dump())
        room=None
        while time.monotonic()<deadline:
            await settle()
            census=await rt.game.query('home/list_rooms',x=x+1,z=z+1,cells=True)
            room=next((r for r in census.get('rooms',[]) if r.get('cellsComplete') is True
                and {(p['x'],p['z']) for p in r.get('cells',[])}==interior),None)
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            complete={(r['position']['x'],r['position']['z']) for r in buildings.get('buildings',[])
                if r.get('status')=='built' and r.get('defName') in ('Wall','Door')}
            if boundary<=complete and room and room.get('properRoom') and room.get('openRoofCount')==0:break
            clock=await window()
            report['samples'].append(dict(clock=clock,built=len(boundary&complete),room=room))
            save();print(json.dumps(dict(tick=clock.get('lastTick'),built=len(boundary&complete),roof=room.get('openRoofCount') if room else None)),flush=True)
        record('pawn_built_and_roofed_nonrectangular_room',boundary<=complete and room is not None and room.get('openRoofCount')==0,
            room=room,buildings=buildings)
        adopted=await command(dict(kind='AdoptRoom',intent_id='native-irregular',bounds=b,entrance='south',
            entrance_cell=dict(x=door[0],z=door[1]),interior_cells=[dict(x=a,z=c) for a,c in sorted(interior)]))
        identity,goal,shell=player_shelter(rt.current_plan)
        record('exact_native_room_adopted',adopted['native_room']==room['id'],adoption=goal.model_dump())
        rt.current_plan.colony_goals.setdefault('EnsureInitialShelter',ColonyGoal(priority_class=2))
        selected=await sleeping_handoff(rt,dict(colonists=2,indoorSleepingCapacity=0),identity,shell)
        assert selected
        furnishings=await command(dict(kind='PlaceBuildings',purpose='shelter',buildings=selected[1][0]))
        observed=await rt.game.query('home/list_rooms',x=x+1,z=z+1,cells=True)
        refreshed=next(r for r in observed['rooms'] if r['id']==room['id'])
        record('native_furnishings_complete_with_continuous_aisle',
            rt.current_plan.progress[furnishings['step']].state=='complete' and len(refreshed['beds'])>=2,
            room=refreshed,aisle=sorted(entrance_aisle(shell,interior)))
        report['checkpoint']=await create_checkpoint(rt,rt.context_token)
        record('zero_inference',rt.counters['model_calls']==0 and NoInference.attempts==0)
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error);report['error_evidence']=getattr(error,'evidence',None)
        if rt.connected:
            try:report['checkpoint']=await create_checkpoint(rt,rt.context_token)
            except Exception as checkpoint_error:report['checkpoint_error']=repr(checkpoint_error)
        raise
    finally:
        report['plan']=rt.current_plan.model_dump(mode='json')
        try:
            if started:await rt.stop()
        finally:
            store.close();(args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=1800)
    args=parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args),args.seconds+120))
