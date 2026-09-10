"""Ordinary research, construction and target-count chemfuel production acceptance."""
import argparse
import asyncio
import json
import shutil
import xml.etree.ElementTree as ET
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
from rimbot.session_checkpoint import prepare_resume


async def run(args):
    if args.checkpoint:
        data,state=prepare_resume(args.checkpoint);root=Path(data['root'])
        args.output.mkdir(parents=True,exist_ok=False)
        config=root/'config-headless';store=Store(state/'bridge.sqlite')
    else:
        root=isolated_root(args.source_root,args.output/'bridge')
        if args.source_save:
            ET.parse(args.source_save)
            shutil.copy2(args.source_save,root/'profile/Saves/RimBot-tribal8-baseline.rws')
        config=prepare(root);store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,resume=args.checkpoint)
    report={'outcome':'failed','cases':[]};fixture_steps=set();food_support_ready=False;deadline=time.monotonic()+args.seconds
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
    async def facts():
        from rimbot.colony_policy import derive
        return derive(rt.batch,await rt.game.query('home/colony_facts',planning=True),rt.controller.policy)
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
        assert all(rt.current_plan.progress[s.id].state in ('pending','complete','waiting') for s in steps)
        fixture_steps.update(s.id for s in steps)
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
        if clock['stopReason']=='force_paused':
            report['dialog']={'ui':await rt.game.invoke('rimworld/get_ui_state',{}),
                'targets':await rt.game.invoke('rimworld/get_screen_targets',{}),
                'research':await rt.game.invoke('home/research',{'filter':'Biofuel','finished':True,'locked':True})}
            from rimbot.session_checkpoint import create_checkpoint
            report['dialog_checkpoint']=await create_checkpoint(rt,rt.context_token)
            save()
        if clock['stopReason'] in ('letter_pause','notification_batch'):
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
        await rt.projects.reconcile(rt.game);rt.reconcile_plan()
        rt.manual_requests.extend((identity,rt.context_token,rt.chat_revision) for identity in fixture_steps
            if rt.current_plan.progress[identity].state=='pending')
        await rt.execute_manual_requests()
        report['latest']={'phase':phase,'clock':clock,'facts':await facts(),
            'research':await rt.game.invoke('home/research',{'filter':'Biofuel','finished':True,'locked':True})}
        save()
        if food_support_ready:
            for identity in ('EnsureBasicDefense','EnsureFoodSupply','EnsureCooking','MaintainResource-WoodLog'):
                for _ in range(4):
                    if not await compile_method(identity):break
    async def wait_build(definition, minimum_count=1):
        while True:
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            built=[b for b in buildings['buildings'] if b['defName']==definition
                and not b.get('isBlueprint') and not b.get('isFrame')]
            if len(built)>=minimum_count:return built[-1]
            await window('build-'+definition)
    async def place(definition,origin,materials=None,minimum_count=1):
        existing=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        found=[b for b in existing['buildings'] if b['defName']==definition and not b.get('isBlueprint') and not b.get('isFrame')]
        if len(found)>=minimum_count:return found[-1]
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
                return await wait_build(definition,minimum_count)
        raise AssertionError('No ordinary native placement for '+definition)
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root,config,rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        while await compile_method('AllowStartingSupplies'):pass
        while await compile_method('EnsureWorkAssignments'):pass
        observed=await facts();initial_center=observed['center'];initial_origin=(initial_center['x'],initial_center['z'])
        await place('ButcherSpot',initial_origin)
        await place('Campfire',(initial_origin[0]+4,initial_origin[1]))
        await command(kind='CreateGoal',goal='MaintainResource',resource='WoodLog',quantity=350)
        food_support_ready=True
        for identity in ('EnsureBasicDefense','EnsureFoodSupply','EnsureCooking','MaintainResource-WoodLog'):
            for _ in range(4):
                if not await compile_method(identity):break
        result=await command(kind='CreateGoal',goal='MaintainResource',resource='Chemfuel',quantity=35)
        identity=result['goal'];goal=rt.current_plan.colony_goals[identity]
        try:await resource_method(rt,identity,await facts())
        except SkillBlocked as error:goal.status,goal.reason='blocked',str(error)
        record('missing_prerequisite_reported',goal.status=='blocked',reason=goal.reason)
        observed=await facts();center=observed['center'];origin=(center['x'],center['z'])
        bench=await place('SimpleResearchBench',origin,['WoodLog'])
        second=await place('SimpleResearchBench',(origin[0]+6,origin[1]),['WoodLog'],minimum_count=2)
        record('ordinary_research_bench_built',bool(bench) and bool(second),benches=[bench,second])
        roster=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True))['pawns']
        candidates=[p for p in roster if any(w['name']=='Research' and not w['disabled'] for w in p['work']['types'])]
        assert candidates,'No capable native researcher'
        candidates=sorted(candidates,key=lambda p:-next((s.get('level',0) for s in p['bio']['skills'] if s['name']=='Intellectual'),0))[:2]
        for candidate in candidates:
            research_work=next(w for w in candidate['work']['types'] if w['name']=='Research')
            if research_work.get('priority')!=1:
                await command(kind='SetWorkPriority',pawn=candidate['thingId'],work_type='Research',priority=1)
            for work in candidate['work']['types']:
                if work['name'] not in ('Research','Firefighter','Patient','PatientBedRest','BedRest') and not work['disabled'] and work.get('priority',0)!=0:
                    await command(kind='SetWorkPriority',pawn=candidate['thingId'],work_type=work['name'],priority=0)
        while await compile_method('EnsureWorkAssignments'):pass
        research=await rt.game.invoke('home/research',{'filter':'Biofuel','finished':True,'locked':True})
        if 'BiofuelRefining' not in research.get('finished',[]):
            await command(kind='SetResearch',project='BiofuelRefining')
            while True:
                await window('research-biofuel')
                research=report['latest']['research']
                if 'BiofuelRefining' in research.get('finished',[]):break
                progress=(research.get('current') or {}).get('progress',0)
                if int(progress//150)>report.get('research_checkpoint_band',0):
                    from rimbot.session_checkpoint import create_checkpoint
                    report['research_checkpoint']=await create_checkpoint(rt,rt.context_token)
                    report['research_checkpoint_band']=int(progress//150)
                    save()
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
        from rimbot.session_checkpoint import create_checkpoint
        report['production_checkpoint']=await create_checkpoint(rt,rt.context_token)
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
    parser.add_argument('--source-save',type=Path,help='Resume an unchanged completed native autosave in a new isolated profile')
    parser.add_argument('--checkpoint',type=Path,help='Resume the immutable paired native/controller research checkpoint')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=2400)
    args=parser.parse_args();asyncio.run(asyncio.wait_for(run(args),args.seconds+180))
