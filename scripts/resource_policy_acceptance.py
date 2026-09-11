"""Ordinary native bill consumption, reserve enforcement and resource acquisition."""
from rimgovernor.native_scenario import advance_game
import argparse
import asyncio
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.headless import isolated_root, prepare, prepare_rendered
from rimgovernor.store import Store
from rimgovernor.player_commands import apply_command
from rimgovernor.colony_plan import ColonyGoal, CommitSteps
from rimgovernor.campaign_manifest import capture_manifest


class MissingFixture(ValueError):
    pass


async def run(args):
    rendered=getattr(args,'rendered',False)
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare_rendered(root) if rendered else prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=not rendered)
    report={'outcome':'failed','cases':[]}
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def command(**payload):
        # Consume the previous bounded clock stop before capturing this new direction.
        async with rt.lock:
            await rt.refresh_clock_events()
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    async def facts(): return await rt.game.query('home/colony_facts',planning=True)
    async def window(ticks=600):
        state = await advance_game(rt, ticks, report, timeout=60)
        async with rt.lock:
            await rt.refresh_clock_events()
        if rt.review_task and not rt.review_task.done():await rt.review_task
        return state
    async def stock(resource):
        value=await rt.game.query('home/list_things',match=resource,ownership='all',includeHeld=False,maxPositionsPerDef=0)
        return sum(r['total'] for r in value['things'] if r['defName']==resource)
    async def bills(bench):return await rt.game.invoke('home/bills',{'action':'list','bench':bench,'dryRun':True})
    try:
        manifest=capture_manifest(Path(__file__).resolve().parents[1],root,config,rt.router.routing.model_dump(mode='json'),
            profile=root/'profile' if rendered else None)
        manifest['runtime_binary_sha256']=manifest['inputs']['artifacts']['runtime_dll']
        (args.output/'manifest.json').write_text(json.dumps(manifest,indent=2))
        await ready(rt)
        from native_scenario_support import allow_starting_supplies, settle_dispatch
        observed=await allow_starting_supplies(rt)
        # A crafting spot is an ordinary zero-work player building; products still require pawn labor.
        for cell in sorted(observed['cells'],key=lambda c:(c['x']-observed['center']['x'])**2+(c['z']-observed['center']['z'])**2):
            if cell['occupied'] or not cell['walkable']:continue
            req={'defName':'CraftingSpot','x':cell['x'],'z':cell['z'],'rotation':'north','dryRun':True}
            preview=await rt.game.invoke('home/place_building',req)
            if preview.get('canPlace') is not True:continue
            await rt.game.invoke('home/place_building',dict(req,dryRun=False),allow_write=True)
            bench='CraftingSpot@'+str(cell['x'])+','+str(cell['z']);break
        else:raise AssertionError('No legal crafting spot')
        recipes=await rt.game.invoke('home/bills',{'action':'recipes','bench':bench,'dryRun':True})
        report['recipes']=recipes
        roster=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True))['pawns']
        for person in roster:
            for work in ('Crafting',):
                if any(w['name']==work and not w['disabled'] for w in person['work']['types']):
                    await command(kind='SetWorkPriority',pawn=person['thingId'],work_type=work,priority=1)
        recipe=next(r for r in recipes['recipes'] if len(r['ingredients'])==1
            and any(c['defName']=='WoodLog' and c['needed']>0 for c in r['ingredients'][0].get('costOptions',[]))
            and len(r['products'])==1 and not r.get('minSkill'))
        needed=next(c['needed'] for c in recipe['ingredients'][0]['costOptions'] if c['defName']=='WoodLog')
        available=next(c['available'] for c in recipe['ingredients'][0]['costOptions'] if c['defName']=='WoodLog')
        output=recipe['products'][0]['defName'];before=await stock('WoodLog');products=await stock(output)
        assert before >= needed*2,(before,needed)
        await command(kind='CreateBill',bench=bench,recipe=recipe['defName'],target_count=products+10)
        await rt.game.invoke('home/bills',{'action':'set','bench':bench,'index':0,'only':'WoodLog','dryRun':False,'watch':False},allow_write=True)
        initial_bill=(await bills(bench))['benches'][0]['bills'][0]
        record('exact_native_recipe_quantity',needed>0 and initial_bill.get('billId'),recipe=recipe,bill=initial_bill)
        await command(kind='ModifyResourcePolicy',resource='WoodLog',spending='stop')
        await window(600)
        record('existing_bill_stopped',await stock('WoodLog')==before and await stock(output)==products,
            before=before,after=await stock('WoodLog'),bill=(await bills(bench))['benches'][0]['bills'][0])
        await command(kind='SetResourceReserve',resource='WoodLog',reserve=available-needed)
        await command(kind='ModifyResourcePolicy',resource='WoodLog',spending='normal')
        deadline=time.monotonic()+args.seconds
        while await stock(output)==products and time.monotonic()<deadline:
            for person in roster:
                try:
                    receipt=await rt.game.invoke('home/order',{'action':'work','pawn':person['thingId'],'target':bench,'dryRun':False,'watch':False},allow_write=True)
                    if receipt.get('success'):break
                except Exception:continue
            await window(600)
        after=await stock('WoodLog');produced=await stock(output)-products
        record('one_cycle_native_consumption',produced==recipe['products'][0]['count'] and before-after==needed,
            input_before=before,input_after=after,quantity=needed,product=output,produced=produced)
        await window(1200)
        record('reserve_stops_further_cycles',await stock('WoodLog')==after and await stock(output)==products+produced)
        saved_bill=(await bills(bench))['benches'][0]['bills'][0]
        record('player_bill_configuration_preserved',saved_bill['config']==initial_bill['config'] or
            all(saved_bill['config'][k]==initial_bill['config'][k] for k in ('repeatMode','targetCount','ingredientSearchRadius')),
            before=initial_bill,after=saved_bill)
        # Every named target is resolved and persists; available mineral/plant sources are native facts.
        for resource in ('Steel','ComponentIndustrial','MedicineIndustrial','MedicineHerbal','Chemfuel','WoodLog'):
            source=await rt.game.invoke('home/resource_sources',{'resource':resource})
            observed=await facts();quantity=observed['resources'].get(resource,0)+1
            result=await command(kind='CreateGoal',goal='MaintainResource',resource=resource,quantity=quantity)
            record('target_'+resource,rt.current_plan.colony_goals[result['goal']].target=={'resource':resource,'quantity':quantity},sources=source)
        if args.acquisition:
            from rimgovernor.production_policy import resource_method
            async def dispatch_acquisition(goal_id, method, actions, observed):
                steps,_=rt.controller.skills.steps(goal_id,method,actions,observed)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Resource target native acquisition',steps=steps).decision(rt.current_plan),
                    actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
                await rt.execute_manual_requests()
                await settle_dispatch(rt, steps)
                record('issued_'+resource,all(rt.current_plan.progress[s.id].state=='complete' for s in steps),actions=actions,
                       progress={s.id:rt.current_plan.progress[s.id].model_dump(mode='json') for s in steps})
            for resource in getattr(args, 'acquisition_resources', None) or ('Steel','ComponentIndustrial','MedicineHerbal','WoodLog'):
                observed=await facts();goal_id='MaintainResource-'+resource
                census=await rt.game.invoke('home/resource_sources',{'resource':resource})
                report.setdefault('acquisition_sources',{})[resource]=census
                if not census.get('sources'):
                    raise MissingFixture('No safely reachable native source for '+resource)
                goal=rt.current_plan.colony_goals[goal_id]
                goal.target['quantity']=observed['resources'].get(resource,0)+1
                before_native=await stock(resource)
                method,actions=await resource_method(rt,goal_id,observed)
                assert actions and all(a['tool']=='home/acquire_resource' for a in actions)
                await dispatch_acquisition(goal_id,method,actions,observed)
                issued_targets={a['arguments']['thingId'] for a in actions}
                people=(await rt.game.query('home/list_pawns',colonistsOnly=True,bio=True,work=True,equipment=True))['pawns']
                rt.current_plan.colony_goals.setdefault('EnsureWorkAssignments',ColonyGoal(priority_class=2,source='PLAYER'))
                assignment=await rt.controller.skills.compile('EnsureWorkAssignments',observed,people)
                if assignment:
                    work_method,work_actions=assignment
                    work_steps,_=rt.controller.skills.steps('EnsureWorkAssignments',work_method,work_actions,observed)
                    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                        reason='Assign native target work',steps=work_steps).decision(rt.current_plan),
                        actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                    for _ in work_steps:
                        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in work_steps
                            if rt.current_plan.progress[s.id].state=='pending')
                        await rt.execute_manual_requests()
                    await settle_dispatch(rt, work_steps)
                    record('assigned_'+resource,all(rt.current_plan.progress[s.id].state=='complete' for s in work_steps),
                        work_types=goal.evidence.get('work_types'),actions=work_actions)

                until=time.monotonic()+args.seconds
                while time.monotonic()<until:
                    await window(600)
                    observed=await facts()
                    report['acquisition_progress']={'resource':resource,'stock':observed['resources'],
                        'tick':observed['tick'],'pawns':await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)}
                    (args.output/'progress.json').write_text(json.dumps(report,indent=2))
                    if observed['resources'].get(resource,0)>=goal.target['quantity']:break
                    # Native yields are estimates: a consumed plant can leave a
                    # deficit. Replan from fresh sources, never replay a target.
                    if len(issued_targets)<8:
                        replanned=await resource_method(rt,goal_id,observed)
                        if not replanned:continue
                        method,actions=replanned
                        if actions and all(a['tool']=='home/acquire_resource' and
                            a['arguments']['thingId'] not in issued_targets for a in actions):
                            report.setdefault('acquisition_replans',[]).append(dict(resource=resource,
                                tick=observed['tick'],stock=observed['resources'].get(resource,0),
                                previous_targets=sorted(issued_targets),actions=actions))
                            await dispatch_acquisition(goal_id,method,actions,observed)
                            issued_targets.update(a['arguments']['thingId'] for a in actions)
                record('native_acquired_'+resource,observed['resources'].get(resource,0)>=goal.target['quantity']
                    and await stock(resource)>before_native,before=before_native,after=await stock(resource),
                    goal=goal.model_dump(mode='json'),tick=observed['tick'])
        if args.persistence:
            from rimgovernor.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
            expected_policy=json.loads(json.dumps(rt.current_plan.control.get('resource_policy',{})))
            expected_native=(await bills(bench))['benches'][0]['bills'][0]['config']
            checkpoint=await create_checkpoint(rt,rt.context_token)
            report['checkpoint']=checkpoint
            await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
            await rt.stop();store.close()
            _,state_root=prepare_resume(checkpoint['manifest_path'])
            store=Store(state_root/'bridge.sqlite')
            rt=BridgeRuntime(store,root,fresh=True,headless=not rendered,resume=checkpoint['manifest_path'])
            await ready(rt)
            record('paired_policy_preserved',rt.mode=='manual' and rt.current_plan.control.get('resource_policy')==expected_policy)
            record('paired_bill_settings_preserved',(await bills(bench))['benches'][0]['bills'][0]['config']==expected_native)
            before_resume=await stock('WoodLog')
            await window(600)
            record('manual_reserve_enforced',await stock('WoodLog')>=expected_policy['WoodLog'].get('reserve',0),
                before=before_resume,after=await stock('WoodLog'),policy=expected_policy)
        record('zero_inference',rt.counters.get('model_calls',0)==0,counters=rt.counters)
        report['outcome']='passed'
    except MissingFixture as error:
        report.update(outcome='missing_prerequisite',error=str(error))
    except Exception as error:
        report['error']=str(error);raise
    finally:
        report['plan']=rt.current_plan.model_dump(mode='json')
        report['counters']=rt.counters
        try:
            if rt.connected:
                rt.session_closing=True
                report['cleanup']=(await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)).model_dump(mode='json')
                rt.owned_game_stopped=True
        finally:
            await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=300)
    parser.add_argument('--acquisition-resources',nargs='+',choices=['Steel','ComponentIndustrial','MedicineHerbal','WoodLog'])
    parser.add_argument('--rendered',action='store_true')
    parser.add_argument('--persistence',action='store_true',help='Require paired native save/load and Manual reserve enforcement')
    parser.add_argument('--acquisition',action='store_true',help='Require actual native mined steel/components and harvested herbal medicine')
    args=parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args),args.seconds*(5 if args.acquisition else 1)+480))
