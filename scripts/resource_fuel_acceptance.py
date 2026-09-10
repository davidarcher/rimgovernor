"""Ordinary research, construction and target-count chemfuel production acceptance."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import runtime_file_read
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.player_commands import apply_command
from rimbot.production_policy import resource_method, refresh_resource_prerequisite
from rimbot.colony_skills import SkillBlocked
from rimbot.campaign_manifest import capture_manifest


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True)
    report={'outcome':'failed','cases':[]};deadline=time.monotonic()+args.seconds
    def save(): (args.output/'result.json').write_text(json.dumps(report,indent=2))
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence));save()
        print(name+': '+str(bool(passed)),flush=True);assert passed,name
    async def command(**payload):
        async with rt.lock:
            rt.clock_events.extend(await rt.supervisor.poll())
            rt.receive_clock_events()
        if rt.review_task and not rt.review_task.done():await rt.review_task
        value=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        for _ in range(128):
            if not rt.manual_requests:break
            await rt.execute_manual_requests()
        return value
    async def facts():return await rt.game.query('home/colony_facts',planning=True)
    async def compile_method(identity, selected=None):
        observed=await facts()
        people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,equipment=True))['pawns']
        goal=rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=2,source='PLAYER'))
        selected=selected or await rt.controller.skills.compile(identity,observed,people)
        if not selected:return False
        method,actions=selected
        if not actions:return False
        steps,_=rt.controller.skills.steps(identity,method,actions,observed)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native resource acceptance: '+identity,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        for _ in steps:
            rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                if rt.current_plan.progress[s.id].state=='pending')
            await rt.execute_manual_requests()
        assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
        return True
    async def window(phase):
        assert time.monotonic()<deadline,'Native fuel acceptance deadline expired'
        if rt.review_task and not rt.review_task.done():await rt.review_task
        await rt.supervisor.change('Superfast',max_ticks=600)
        async with asyncio.timeout(45):
            while True:
                clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                if not clock['active']:break
                await asyncio.sleep(.15)
        if clock['stopReason']=='letter_pause':
            danger=await rt.game.query('home/status',colonists=False,threats=True)
            record('observed_notification',clock['pauseVerified'] and danger['counts']['hostileCount']==0
                and danger['counts']['huntingPredatorCount']==0,clock=clock,danger=danger)
            rt.supervisor.absorb(clock);rt.supervisor.allow_resume()
        else:assert clock['pauseVerified'] and clock['stopReason'] in ('tick_budget','requested_pause'),clock
        if rt.review_task and not rt.review_task.done():await rt.review_task
        async with rt.lock:
            rt.clock_events.extend(await rt.supervisor.poll())
            rt.receive_clock_events()
        if rt.review_task and not rt.review_task.done():await rt.review_task
        report['latest']={'phase':phase,'clock':clock,'facts':await facts(),
            'research':await rt.game.invoke('home/research',{'filter':'Biofuel','finished':True,'locked':True})}
        save()
    async def wait_build(definition):
        while True:
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            built=next((b for b in buildings['buildings'] if b['defName']==definition
                and not b.get('isBlueprint') and not b.get('isFrame')),None)
            if built:return built
            await window('build-'+definition)
    async def place(definition,origin,materials=None):
        observed=await facts()
        cells=sorted(observed['cells'],key=lambda c:(c['x']-origin[0])**2+(c['z']-origin[1])**2)
        for cell in cells:
            if cell['occupied'] or not cell['walkable']:continue
            arguments={'defName':definition,'x':cell['x'],'z':cell['z'],'rotation':'north','dryRun':True}
            if materials:arguments['stuff']=materials[0]
            result=await rt.game.invoke('home/place_building',arguments)
            if result.get('canPlace'):
                placement={'def_name':definition,'x':cell['x'],'z':cell['z']}
                if materials:placement['materials']=materials
                await command(kind='PlaceBuildings',buildings={'kind':'place_buildings','placements':[placement]})
                return await wait_build(definition)
        raise AssertionError('No ordinary native placement for '+definition)
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root,config,rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        while await compile_method('AllowStartingSupplies'):pass
        while await compile_method('EnsureWorkAssignments'):pass
        result=await command(kind='CreateGoal',goal='MaintainResource',resource='Chemfuel',quantity=35)
        identity=result['goal'];goal=rt.current_plan.colony_goals[identity]
        try:await resource_method(rt,identity,await facts())
        except SkillBlocked as error:goal.status,goal.reason='blocked',str(error)
        record('missing_prerequisite_reported',goal.status=='blocked',reason=goal.reason)
        observed=await facts();center=observed['center'];origin=(center['x'],center['z'])
        bench=await place('SimpleResearchBench',origin,['WoodLog'])
        record('ordinary_research_bench_built',bool(bench),bench=bench)
        roster=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True))['pawns']
        candidates=[p for p in roster if any(w['name']=='Research' and not w['disabled'] for w in p['work']['types'])]
        assert candidates,'No capable native researcher'
        candidate=max(candidates,key=lambda p:next((s.get('level',0) for s in p['bio']['skills'] if s['name']=='Intellectual'),0))
        await command(kind='SetWorkPriority',pawn=candidate['thingId'],work_type='Research',priority=1)
        for work in candidate['work']['types']:
            if work['name'] not in ('Research','Firefighter','Patient','PatientBedRest','BedRest') and not work['disabled']:
                await command(kind='SetWorkPriority',pawn=candidate['thingId'],work_type=work['name'],priority=0)
        research=await rt.game.invoke('home/research',{'filter':'Biofuel','finished':True,'locked':True})
        if 'BiofuelRefining' not in research.get('finished',[]):
            await command(kind='SetResearch',project='BiofuelRefining')
            while True:
                await window('research-biofuel')
                research=report['latest']['research']
                if 'BiofuelRefining' in research.get('finished',[]):break
        record('ordinary_biofuel_research_completed',True,research=research)
        generator=await place('WoodFiredGenerator',origin)
        position=generator['position'];refinery=await place('BiofuelRefinery',(position['x']+3,position['z']))
        record('ordinary_refinery_built',bool(refinery),refinery=refinery,generator=generator)
        record('native_prerequisite_recovered',await refresh_resource_prerequisite(rt,identity,await facts()),goal=goal.model_dump(mode='json'))
        selected=await resource_method(rt,identity,await facts());assert selected
        await compile_method(identity,selected)
        while await compile_method('EnsureWorkAssignments'):pass
        before=(await facts()).get('resources',{}).get('Chemfuel',0)
        while (await facts()).get('resources',{}).get('Chemfuel',0)<35:await window('produce-chemfuel')
        after=(await facts())['resources']['Chemfuel']
        record('native_chemfuel_produced',after>before,before=before,after=after,
            bills=await rt.game.invoke('home/bills',{'action':'list','dryRun':True}))
        record('zero_inference',rt.counters.get('model_calls',0)==0,counters=rt.counters)
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error);report['error_evidence']=getattr(error,'evidence',None);raise
    finally:
        report['plan']=rt.current_plan.model_dump(mode='json')
        try:
            if rt.connected:
                rt.session_closing=True
                report['cleanup']=(await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)).model_dump(mode='json')
                rt.owned_game_stopped=True
        finally:await rt.stop();store.close();save()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=2400)
    args=parser.parse_args();asyncio.run(asyncio.wait_for(run(args),args.seconds+180))
