"""Observe ordinary hunting and butchering through shared Hands in a private colony."""
import argparse
import asyncio
import json
import time
import traceback
import shutil
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_skills import native
from rimbot.headless import isolated_root, prepare
from rimbot.hunting import screen_prey
from rimbot.player_commands import apply_command
from rimbot.store import Store


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge')
    if args.checkpoint:
        shutil.copy2(args.checkpoint,root/'profile/Saves/RimBot-tribal8-baseline.rws')
    config=prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','cases':[], 'samples':[],
            'start_type':'saved_checkpoint' if args.checkpoint else 'fresh_baseline'}
    NoInference.attempts=0

    def save():
        (args.output/'progress.json').write_text(json.dumps(report,indent=2),encoding='utf8')

    async def facts():
        return await rt.game.query('home/colony_facts',planning=True)

    async def roster():
        return (await rt.game.query('home/list_pawns',colonistsOnly=True,work=True,
                                   bio=True,equipment=True))['pawns']

    async def issue(goal_id,method,actions,observed):
        async with rt.lock:await rt.refresh_clock_events()
        rt.controller.finish_review()
        goal=rt.current_plan.colony_goals.setdefault(goal_id,ColonyGoal(priority_class=2,source='PLAYER'))
        steps,_=rt.controller.skills.steps(goal_id,method,actions,observed)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native food acceptance: '+method,steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        goal.steps.extend(s.id for s in steps)
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        failed=[rt.current_plan.progress[s.id].model_dump() for s in steps
                if rt.current_plan.progress[s.id].state in ('blocked','cancelled')]
        assert not failed,failed
        return steps

    async def observe_spoilage(meat,require_sharing=True):
        watched=(await rt.bridge.call('test/food_observe',target=meat['id'])).structuredContent
        target=next(s for s in watched['watched'] if s['id']==meat['id'])
        if not target['forbidden']:
            forbid=await rt.controller.skills.designator('Designator_Forbid')
            await issue('FoodSpoilageObservation','reserve-sample',[native(
                'rimworld/apply_architect_designator',designatorId=forbid,x=target['x'],z=target['z'],
                keepSelected=False)],await facts())
        if args.vary_temperature:
            buildings=await rt.game.query('home/list_buildings',match='Campfire',playerOnly=True,aggregate=False)
            heaters=[b for b in buildings['buildings'] if b.get('defName')=='Campfire' and b.get('status')=='built']
            assert len(heaters)==1,heaters
            heater=heaters[0]
            deconstruct=await rt.controller.skills.designator('Designator_Deconstruct')
            await issue('FoodSpoilageObservation','remove-heat-source',[native(
                'rimworld/apply_architect_designator',designatorId=deconstruct,**heater['position'],
                keepSelected=False)],await facts())
            report['thermal_work']=heater
        report['spoilage']=[]
        deadline=time.monotonic()+args.seconds
        while time.monotonic()<deadline:
            state=(await rt.bridge.call('test/food_observe')).structuredContent
            sample=next(s for s in state['watched'] if s['id']==meat['id'])
            current=await facts()
            report['spoilage'].append(dict(tick=state['tick'],native=sample,
                available=any(s['id']==meat['id'] for s in current['foodSupply']['stocks'])))
            report['ingestion']=state['meals'];save()
            assert not state['truncated'] and not report['spoilage'][-1]['available']
            if sample['stage']=='Rotting':
                assert sample['destroyed'],sample
                temperatures=[s['native']['temperature'] for s in report['spoilage'] if s['native']['temperature'] is not None]
                assert max(temperatures)-min(temperatures)>1,temperatures
                shared={}
                colonists={p['thingId'] for p in await roster()}
                for meal in state['meals']:
                    if meal['pawn'] in colonists:shared.setdefault(meal['origin'],set()).add(meal['pawn'])
                if require_sharing:
                    assert any(len(pawns)>1 for pawns in shared.values()),'No shared food origin consumed by multiple pawns'
                report['cases'].append(dict(name='Ordinary spoilage with changing temperatures',
                    shared_consumption_checked=require_sharing,
                    food=meat['id'],temperature_range=[min(temperatures),max(temperatures)],
                    shared_origins={k:sorted(v) for k,v in shared.items() if len(v)>1}))
                return
            assert sample['spawned'] and sample['forbidden'],sample
            await window()
        raise AssertionError('Native spoilage not observed within the time bound')

    async def window():
        if rt.review_task and not rt.review_task.done():await rt.review_task
        await rt.supervisor.change('Superfast',max_ticks=600)
        async with asyncio.timeout(45):
            while True:
                state=(await rt.bridge.call('home/supervised_play',op='status')).structuredContent
                if not state['active']:break
                await asyncio.sleep(.2)
        report['last_clock']=state
        if state['stopReason']=='letter_pause' and 'Ancient danger' in state.get('stopDetail',''):
            danger=await rt.game.query('home/status',colonists=False,threats=True)
            letters=await rt.game.invoke('rimworld/list_letters',{})
            assert state['pauseVerified'] and danger['counts']['hostileCount']==0 and danger['counts']['huntingPredatorCount']==0,danger
            report['cases'].append(dict(name='Observed sealed ancient-danger warning',clock=state,letters=letters,danger=danger))
            rt.supervisor.absorb(state);rt.supervisor.allow_resume()
        else:
            assert state['pauseVerified'] and state['stopReason'] in ('tick_budget','requested_pause'),state
        async with rt.lock:await rt.refresh_clock_events()
        rt.controller.finish_review()
        save()
        if rt.review_task and not rt.review_task.done():await rt.review_task

    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,
            {'model':'no inference'},source_snapshot=args.source_snapshot)
        await ready(rt)
        if rt.review_task and not rt.review_task.done():await rt.review_task
        rt.execution_task=asyncio.current_task()
        if args.spoilage:
            report['observer']=(await rt.bridge.call('test/food_observe')).structuredContent
        if args.observe_food_id:
            assert args.checkpoint and args.spoilage
            await observe_spoilage({'id':args.observe_food_id},require_sharing=False)
            report.update(outcome='passed',scope='Native temperature response and rot of unchanged autosaved food')
        else:
            token=rt.context_token
            for identity in ('AllowStartingSupplies','EnsureWorkAssignments','EnsureBasicDefense','EnsureWorkAssignments'):
                report['setup']=identity;save()
                rt.current_plan.colony_goals.setdefault(identity,ColonyGoal(priority_class=2,source='PLAYER'))
                for _ in range(12):
                    observed=await facts();people=await roster()
                    if identity=='AllowStartingSupplies' and not observed.get('forbiddenSupplies'):break
                    observed['armed']=sum(p.get('equipment',{}).get('armed') is True for p in people)
                    if identity=='EnsureBasicDefense' and observed['armed']>=2:break
                    compiled=await rt.controller.skills.compile(identity,observed,people)
                    if compiled is None or not compiled[1]:break
                    steps=await issue(identity,*compiled,observed)
                    for _ in range(20):
                        if all(rt.current_plan.progress[s.id].state=='complete' for s in steps):break
                        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                            if rt.current_plan.progress[s.id].state=='pending')
                        await rt.execute_manual_requests()
                        await window()
                    assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps),{
                        s.id:rt.current_plan.progress[s.id].model_dump() for s in steps}

            observed=await facts()
            report['workers']=[dict(id=p['thingId'],equipment=p.get('equipment'),
                hunting=[w for w in p.get('work',{}).get('types',[]) if w['name']=='Hunting']) for p in await roster()]
            wildlife=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
            candidates,screen=screen_prey(wildlife['pawns'],observed['center'])
            report['wildlife']=wildlife;report['screen']=screen;save()
            assert candidates,'No native prey passes the hunter-route screen'
            prey=candidates[0];report['prey']=prey

            for cell in sorted(observed['cells'],key=lambda c:(c['x']-observed['center']['x'])**2+(c['z']-observed['center']['z'])**2):
                if cell['occupied'] or not cell['walkable']:continue
                placement=dict(def_name='ButcherSpot',x=cell['x'],z=cell['z'])
                preview=await rt.inspect_native('home/place_building',dict(defName='ButcherSpot',
                    x=cell['x'],z=cell['z'],rotation='north',dryRun=True))
                if preview.get('canPlace') is not True:continue
                await apply_command(rt,dict(kind='PlaceBuildings',buildings=dict(kind='place_buildings',placements=[placement])),
                    token=rt.context_token,revision=rt.chat_revision)
                await rt.execute_manual_requests()
                break
            else:raise AssertionError('No ordinary butcher spot placement')
            observed=await facts();assert observed['butchering']
            await issue('EnsureFoodSupply','butcher-bill',[native('home/bills',action='add',
                bench=observed['butchering'][0]['id'],recipe='ButcherCorpseFlesh',repeatMode='Forever',
                ingredientSearchRadius=40,watch=False)],observed)
            designator=await rt.controller.skills.designator('Designator_Hunt')
            steps=await issue('EnsureFoodSupply','hunt-'+prey['thingId'],[
                native('rimworld/apply_architect_designator',designatorId=designator,
                       x=prey['position']['x'],z=prey['position']['z'],keepSelected=False)],observed)
            assert rt.current_plan.progress[steps[0].id].state=='complete'
            report['designation']=rt.current_plan.progress[steps[0].id].model_dump()
            initial={s['defName']:0 for s in observed['foodSupply']['stocks']}
            for stock in observed['foodSupply']['stocks']:initial[stock['defName']]+=stock['count']
            deadline=time.monotonic()+args.seconds
            while time.monotonic()<deadline:
                await window()
                current=await facts();people=await roster()
                wildlife=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
                live=next((p for p in wildlife['pawns'] if p['thingId']==prey['thingId']),None)
                sample=dict(tick=current['tick'],prey=live,foodSupply=current['foodSupply'],
                            corpses=current['foodCorpses'],
                            pawns=people,butchering=current['butchering'])
                report['samples'].append(sample);save()
                assert rt.context_token==token and rt.mode=='manual'
                assert not NoInference.attempts and rt.counters['model_calls']==0
                meat=[s for s in current['foodSupply']['stocks'] if s['defName']==prey['huntingSafety']['meatDef']
                      and s['count']>initial.get(s['defName'],0)]
                saw_corpse=any(any(c['prey']==prey['thingId'] for c in s['corpses']) for s in report['samples'])
                if live is None and saw_corpse and meat and not any(c['prey']==prey['thingId'] for c in current['foodCorpses']):
                    if args.spoilage:await observe_spoilage(meat[0])
                    report.update(outcome='passed',produced_meat=meat)
                    break
            else:raise AssertionError('No observed butchered food within acceptance time bound')
    except Exception as error:
        report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        rt.execution_task=None
        if hasattr(rt,'task'):
            try:await rt.stop()
            except Exception as error:report['cleanup_error']=str(error)
        report['model_calls']=rt.counters['model_calls']
        report['model_attempts']=NoInference.attempts
        store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps({k:report.get(k) for k in ('outcome','error','cleanup_error')}),flush=True)
    return report['outcome']=='passed' and 'cleanup_error' not in report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--source-snapshot',action='store_true')
    parser.add_argument('--checkpoint',type=Path,help='Unmodified native save for targeted food acceptance')
    parser.add_argument('--spoilage',action='store_true',help='With FoodObservationFixture, forbid one ordinarily butchered stack and observe actual rot, temperature variation and shared-stock ingestion')
    parser.add_argument('--observe-food-id',help='Observe an existing perishable stack in an unmodified checkpoint; skips hunting and shared-ingestion assertions')
    parser.add_argument('--vary-temperature',action='store_true',help='Queue ordinary deconstruction of the observed campfire to measure food temperature response')
    parser.add_argument('--seconds',type=int,default=900)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
