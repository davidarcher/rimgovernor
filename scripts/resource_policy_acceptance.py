"""Ordinary native bill consumption, reserve enforcement and resource acquisition."""
import argparse
import asyncio
import hashlib
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.campaign_manifest import capture_manifest


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True)
    report={'outcome':'failed','cases':[]}
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def command(**payload):
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    async def facts(): return await rt.game.query('home/colony_facts',planning=True)
    async def clock_status():
        # GABS can briefly race its runtime-state publication on Windows. Retry
        # only this read; never replay a native write after an uncertain result.
        for attempt in range(8):
            try:return (await rt.bridge.call('home/supervised_play',op='status')).structuredContent
            except Exception as error:
                if 'failed to publish runtime state' not in str(error) or attempt==7:raise
                await asyncio.sleep(.2)
    async def window(ticks=600):
        if rt.review_task and not rt.review_task.done():await rt.review_task
        await rt.supervisor.change('Superfast',max_ticks=ticks)
        async with asyncio.timeout(40):
            while True:
                state=await clock_status()
                if not state['active']:break
                await asyncio.sleep(.1)
        if state['stopReason']=='letter_pause':
            letters=await rt.game.invoke('rimworld/list_letters', {})
            danger=await rt.game.query('home/status',colonists=False,threats=True)
            record('letter_observed',state['pauseVerified'] and danger['counts']['hostileCount']==0
                and danger['counts']['huntingPredatorCount']==0,clock=state,letters=letters)
            rt.supervisor.absorb(state);rt.supervisor.allow_resume()
        else: assert state['stopReason'] in ('tick_budget','requested_pause') and state['pauseVerified'],state
        if rt.review_task and not rt.review_task.done():await rt.review_task
        return state
    async def stock(resource):
        value=await rt.game.query('home/list_things',match=resource,ownership='all',includeHeld=False,maxPositionsPerDef=0)
        return sum(r['total'] for r in value['things'] if r['defName']==resource)
    async def bills(bench):return await rt.game.invoke('home/bills',{'action':'list','bench':bench,'dryRun':True})
    try:
        manifest=capture_manifest(Path(__file__).resolve().parents[1],root,config,rt.router.routing.model_dump(mode='json'))
        installed=Path('C:/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/RimBotObservations/Assemblies/RimBot.ColonyIdentity.dll')
        manifest['identity_binary_sha256']=hashlib.sha256(installed.read_bytes()).hexdigest()
        (args.output/'manifest.json').write_text(json.dumps(manifest,indent=2))
        await ready(rt)
        observed=await facts()
        # The production skill uses the same ordinary starting-supply permission as the foothold.
        while observed.get('forbiddenSupplies'):
            goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
            method,actions=await rt.controller.skills.compile('AllowStartingSupplies',observed,[])
            steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,observed)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Allow ordinary starting supplies',steps=steps).decision(rt.current_plan),actor='strategist',
                expected_token=rt.context_token,expected_revision=rt.chat_revision)
            for _ in steps:
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps if rt.current_plan.progress[s.id].state=='pending')
                await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
            observed=await facts()
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
            for work in ('Crafting','Mining','PlantCutting','Growing'):
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
            from rimbot.production_policy import resource_method
            for resource in ('Steel','ComponentIndustrial','MedicineHerbal'):
                observed=await facts();goal_id='MaintainResource-'+resource
                goal=rt.current_plan.colony_goals[goal_id]
                goal.target['quantity']=observed['resources'].get(resource,0)+1
                before_native=await stock(resource)
                method,actions=await resource_method(rt,goal_id,observed)
                assert actions and all(a['tool']=='home/acquire_resource' for a in actions)
                steps,_=rt.controller.skills.steps(goal_id,method,actions,observed)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Resource target native acquisition',steps=steps).decision(rt.current_plan),
                    actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
                await rt.execute_manual_requests()
                record('issued_'+resource,all(rt.current_plan.progress[s.id].state=='complete' for s in steps),actions=actions)
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
                record('native_acquired_'+resource,observed['resources'].get(resource,0)>=goal.target['quantity']
                    and await stock(resource)>before_native,before=before_native,after=await stock(resource),
                    goal=goal.model_dump(mode='json'),tick=observed['tick'])
        record('zero_inference',rt.counters.get('model_calls',0)==0,counters=rt.counters)
        report['outcome']='passed'
    except Exception as error:
        report['error']=str(error);raise
    finally:
        report['plan']=rt.current_plan.model_dump(mode='json')
        report['counters']=rt.counters
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=300)
    parser.add_argument('--acquisition',action='store_true',help='Require actual native mined steel/components and harvested herbal medicine')
    args=parser.parse_args()
    asyncio.run(asyncio.wait_for(run(args),args.seconds*(4 if args.acquisition else 1)+240))
