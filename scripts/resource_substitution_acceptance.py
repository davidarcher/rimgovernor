"""Observe reserve-aware material substitution through actual ordinary construction."""
import argparse
import asyncio
import json
import inspect
import hashlib
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import runtime_file_read
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import prepare_resume
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.production_policy import resource_method
from rimbot.colony_skills import native


async def run(args):
    data,state=prepare_resume(args.checkpoint);root=Path(data['root'])
    args.output.mkdir(parents=True,exist_ok=False)
    store=Store(state/'bridge.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True,resume=args.checkpoint)
    report={'outcome':'failed','checkpoint':str(args.checkpoint),'checkpoint_data':data,'cases':[],
        'harness_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        print(name+': '+str(bool(passed)),flush=True);assert passed,name
    async def command(**payload):
        async with rt.lock:
            rt.clock_events.extend(await rt.supervisor.poll());rt.receive_clock_events()
        if rt.review_task and not rt.review_task.done():await rt.review_task
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests();return result
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(inspect.getfile(BridgeRuntime)).resolve().parents[2],
            root,root/'config-headless',rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        facts=await rt.game.query('home/colony_facts',planning=True)
        wood=facts['resources']['WoodLog'];steel=facts['resources']['Steel']
        await command(kind='SetResourceReserve',resource='WoodLog',reserve=wood)
        chosen=None
        for cell in sorted(facts['cells'],key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2):
            if cell['occupied'] or not cell['walkable']:continue
            previews=[await rt.game.invoke('home/place_building',dict(defName='Wall',x=cell['x'],z=cell['z'],
                rotation='north',stuff=stuff,dryRun=True)) for stuff in ('WoodLog','Steel')]
            if all(p.get('canPlace') for p in previews):chosen=cell;break
        assert chosen,'No normal wall footprint accepts both native materials'
        result=await command(kind='PlaceBuildings',buildings={'kind':'place_buildings','placements':[
            {'def_name':'Wall','x':chosen['x'],'z':chosen['z'],'materials':['WoodLog','Steel']}]})
        step=next(s for s in rt.current_plan.spec.steps if s.id==result['step'])
        record('native_budget_selected_steel',step.action.placements[0].materials==['Steel'],
            step=step.model_dump(mode='json'),wood_reserve=wood)
        deadline=time.monotonic()+args.seconds
        while True:
            await rt.projects.reconcile(rt.game);rt.reconcile_plan()
            progress=rt.current_plan.progress[step.id]
            if progress.state=='complete':break
            assert progress.state!='blocked',progress.model_dump(mode='json')
            assert time.monotonic()<deadline,'Ordinary wall work did not complete'
            if rt.review_task and not rt.review_task.done():await rt.review_task
            await rt.supervisor.change('Superfast',max_ticks=600)
            async with asyncio.timeout(45):
                while True:
                    clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                    if not clock['active']:break
                    await asyncio.sleep(.15)
            if clock['stopReason']=='letter_pause':
                threats=await rt.game.query('home/status',colonists=False,threats=True)
                assert threats['counts']['hostileCount']==0 and threats['counts']['huntingPredatorCount']==0
                rt.supervisor.absorb(clock);rt.supervisor.allow_resume()
            else:assert clock['pauseVerified'] and clock['stopReason'] in ('tick_budget','requested_pause'),clock
            report['latest']={'clock':clock,'progress':progress.model_dump(mode='json')}
            (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        final=await rt.game.query('home/colony_facts',planning=True)
        buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        wall=next((b for b in buildings['buildings'] if b.get('position')=={'x':chosen['x'],'z':chosen['z']}
            and b.get('defName')=='Wall' and b.get('stuff')=='Steel' and not b.get('isBlueprint') and not b.get('isFrame')),None)
        record('ordinary_substituted_wall_completed',bool(wall) and final['resources']['Steel']<steel and final['resources']['WoodLog']>=wood,
            before={'WoodLog':wood,'Steel':steel},after=final['resources'],progress=progress.model_dump(mode='json'),
            wall=wall,buildings=buildings)

        if args.capacity:
            resource='MeleeWeapon_Club'
            created=await command(kind='CreateGoal',goal='MaintainResource',resource=resource,quantity=3)
            goal_id=created['goal']
            observed=await rt.game.query('home/colony_facts',planning=True)
            record('existing_adequate_bill_covers_target',await resource_method(rt,goal_id,observed) is None)
            listing=await rt.game.invoke('home/bills',{'action':'list','dryRun':True})
            bench=next(b for b in listing['benches'] if any(any(p.get('defName')==resource for p in bill.get('products',[])) for bill in b['bills']))
            existing=next(b for b in bench['bills'] if any(p.get('defName')==resource for p in b.get('products',[])))
            async def issue(identity,method,actions):
                rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=3,source='PLAYER'))
                observed=await rt.game.query('home/colony_facts',planning=True)
                steps,_=rt.controller.skills.steps(identity,method,actions,observed)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Explicit native bill-capacity acceptance',steps=steps).decision(rt.current_plan),
                    actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                for _ in steps:
                    rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps if rt.current_plan.progress[s.id].state=='pending')
                    await rt.execute_manual_requests()
                assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            await issue('intent-capacity-fixture','lower-test-bill-target',[native('home/bills',action='set',
                bench=bench['thingId'],index=existing['index'],repeatMode='TargetCount',targetCount=2,
                unpauseWhenYouHave=1,pauseWhenSatisfied='on',watch=False)])
            before_bill=(await rt.game.invoke('home/bills',{'action':'list','bench':bench['thingId'],'dryRun':True}))['benches'][0]['bills'][0]
            selected=await resource_method(rt,goal_id,await rt.game.query('home/colony_facts',planning=True))
            record('inadequate_bill_requires_new_capacity',bool(selected),selected=selected,existing=before_bill)
            await issue(goal_id,*selected)
            await command(kind='SetResourceReserve',resource='WoodLog',reserve=0)
            deadline=time.monotonic()+args.seconds
            while True:
                observed=await rt.game.query('home/colony_facts',planning=True)
                if observed['resources'].get(resource,0)>=3:break
                assert time.monotonic()<deadline,'Expanded target-count capacity did not produce native output'
                if rt.review_task and not rt.review_task.done():await rt.review_task
                await rt.supervisor.change('Superfast',max_ticks=600)
                async with asyncio.timeout(45):
                    while True:
                        clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                        if not clock['active']:break
                        await asyncio.sleep(.15)
                assert clock['pauseVerified'] and clock['stopReason'] in ('tick_budget','requested_pause'),clock
            after_bills=(await rt.game.invoke('home/bills',{'action':'list','bench':bench['thingId'],'dryRun':True}))['benches'][0]['bills']
            after_existing=next(b for b in after_bills if b['billId']==before_bill['billId'])
            record('native_expanded_capacity_produced',observed['resources'][resource]>=3
                and all(after_existing['config'][key]==before_bill['config'][key] for key in (
                    'repeatMode','targetCount','ingredientSearchRadius','pauseWhenSatisfied','unpauseWhenYouHave')),
                stock=observed['resources'],bills=after_bills)

            if args.lease:
                await command(kind='CreateGoal',goal='MaintainResource',resource=resource,quantity=4)
                selected=await resource_method(rt,goal_id,await rt.game.query('home/colony_facts',planning=True))
                assert selected
                await issue(goal_id,*selected)
                before_lease=await rt.game.query('home/colony_facts',planning=True)
                held=before_lease['resources']['WoodLog']
                report['temporary_commitment']=(await rt.bridge.call('home/production_policy',
                    **{k:rt.identity[k] for k in ('colonyId','loadToken','mapId')},
                    floors='',commitments='WoodLog='+str(held),stopped='',dryRun=False)).structuredContent
                await rt.supervisor.change('Superfast',max_ticks=1200)
                async with asyncio.timeout(45):
                    while True:
                        clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                        if not clock['active']:break
                        await asyncio.sleep(.15)
                assert clock['pauseVerified'] and clock['stopReason'] in ('tick_budget','requested_pause'),clock
                after_lease=await rt.game.query('home/colony_facts',planning=True)
                record('native_lease_commitment_stops_bill',after_lease['resources'].get(resource,0)==3
                    and after_lease['resources']['WoodLog']==held,commitment=report['temporary_commitment'],clock=clock)
                async with rt.lock:
                    rt.clock_events.extend(await rt.supervisor.poll());rt.receive_clock_events()
                if rt.review_task and not rt.review_task.done():await rt.review_task
                assert rt.mode=='manual'
                report['manual_clock_start']=(await rt.bridge.call('rimworld/set_time_speed',speed='Superfast')).model_dump(mode='json')
                try:
                    deadline=time.monotonic()+args.seconds
                    while True:
                        observed=await rt.game.query('home/colony_facts',planning=True)
                        if observed['resources'].get(resource,0)>=4:break
                        assert time.monotonic()<deadline,'Ordinary Manual play remained constrained by expired lease commitments'
                        await asyncio.sleep(.15)
                finally:
                    await rt.bridge.call('rimworld/set_time_speed',speed='Paused')
                manual_clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                record('native_manual_play_releases_lease_budget',observed['resources'].get(resource,0)>=4
                    and observed['resources']['WoodLog']<held and not manual_clock['active'],
                    before=after_lease['resources'],after=observed['resources'],clock=manual_clock)
        record('zero_inference',rt.counters['model_calls']==0,counters=rt.counters)
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
        finally:
            await rt.stop();store.close();(args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--lease',action='store_true',help='With --capacity, require lease-only budget enforcement and actual ordinary Manual production afterward')
    parser.add_argument('--capacity',action='store_true',help='Verify existing-bill coverage and actual output after increasing native bill capacity')
    parser.add_argument('--seconds',type=int,default=300)
    args=parser.parse_args()
    if args.lease and not args.capacity:parser.error('--lease requires --capacity')
    asyncio.run(asyncio.wait_for(run(args),args.seconds*3+180))
