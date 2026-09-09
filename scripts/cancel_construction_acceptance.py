"""Native construction cancellation, shared execution and optional local chat acceptance."""
import argparse
import asyncio
import json
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import BridgeError
from rimbot.headless import isolated_root,prepare
from rimbot.store import Store
from rimbot.player_commands import apply_command
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.campaign_manifest import capture_manifest
from rimbot.config import Settings


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');configuration=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True,
        settings=Settings(model=args.model,timeout_seconds=90) if args.chat else None)
    report={'outcome':'failed','cases':[],'scope':'Native blueprint cancellation only; no frame-refund or semantic acceptance'}
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def listed():
        return await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    async def cancel(arguments):
        return (await rt.bridge.call('home/cancel_construction',**arguments)).structuredContent
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root,configuration,rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        facts=await rt.game.query('home/colony_facts',planning=True)
        candidates=sorted((c for c in facts['cells'] if c['walkable'] and not c['occupied']),
            key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2)
        targets=[]
        for definition in ('Wall','Wall','SleepingSpot'):
            for cell in candidates:
                if any(t['position']['x']==cell['x'] and t['position']['z']==cell['z'] for t in targets):continue
                request={'defName':definition,'x':cell['x'],'z':cell['z'],'rotation':'north','dryRun':True}
                if definition=='Wall':request['stuff']='WoodLog'
                preview=await rt.game.invoke('home/place_building',request)
                if preview.get('canPlace') is not True:continue
                await rt.game.invoke('home/place_building',dict(request,dryRun=False),allow_write=True)
                observed=await listed()
                target=next(b for b in observed['buildings'] if b['position']['x']==cell['x'] and b['position']['z']==cell['z']
                    and (b.get('buildDefName') or b['defName'])==definition)
                targets.append(target);break
            else:raise AssertionError('No native legal fixture placement')
        identity=rt.identity
        target,neighbor,completed=targets
        arguments={'colonyId':identity['colonyId'],'loadToken':identity['loadToken'],'mapId':identity['mapId'],
            'thing':target['thingId'],'expectedDef':'Wall','expectedStuff':'WoodLog',
            'x':target['position']['x'],'z':target['position']['z'],'dryRun':True}
        preview=await cancel(arguments)
        record('preview_preserves_order',preview.get('success') and not preview.get('applied')
            and any(b['thingId']==target['thingId'] for b in (await listed())['buildings']),receipt=preview)
        for name,change in [('load',{'loadToken':'stale'}),('map',{'mapId':-1}),('colony',{'colonyId':'different'}),
                            ('position',{'x':arguments['x']+1}),('definition',{'expectedDef':'Door'}),
                            ('material',{'expectedStuff':'Steel'}),('completed',{'thing':completed['thingId'],
                                'expectedDef':'SleepingSpot','expectedStuff':'','x':completed['position']['x'],'z':completed['position']['z']})]:
            refused=False
            try:await cancel(dict(arguments,**change,dryRun=False))
            except BridgeError:refused=True
            present={b['thingId'] for b in (await listed())['buildings']}
            record('refuse_'+name,refused and all(t['thingId'] in present for t in targets))
        receipt=await cancel(dict(arguments,dryRun=False))
        present={b['thingId'] for b in (await listed())['buildings']}
        record('cancel_exact_blueprint',receipt.get('removed') is True and target['thingId'] not in present
            and neighbor['thingId'] in present and completed['thingId'] in present,receipt=receipt)
        refused=False
        try:await cancel(dict(arguments,dryRun=False))
        except BridgeError:refused=True
        record('repeat_refuses_without_retargeting',refused and neighbor['thingId'] in {b['thingId'] for b in (await listed())['buildings']})
        if args.shared:
            facts=await rt.game.query('home/colony_facts',planning=True)
            while facts.get('forbiddenSupplies'):
                goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
                method,actions=await rt.controller.skills.compile('AllowStartingSupplies',facts,[])
                steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Allow starting supplies for cancellation acceptance',steps=steps).decision(rt.current_plan),
                    actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
                for _ in steps:
                    rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                        if rt.current_plan.progress[s.id].state=='pending')
                    await rt.execute_manual_requests()
                assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
                goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
                facts=await rt.game.query('home/colony_facts',planning=True)
            shell=rt.controller.skills.shell(await rt.controller.skills.layout(facts))
            async def command(**payload):
                return await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
            placed=await command(kind='BuildRoom',intent_id='cancel-room',room=shell,purpose='shelter')
            await rt.execute_manual_requests()
            issued=rt.current_plan.progress[placed['step']]
            record('shared_room_orders_issued',issued.state=='waiting' and len(issued.issued)>=12,
                progress=issued.model_dump())
            requested=await command(kind='CancelConstruction',intent_id='cancel-room')
            step=next(s for s in rt.current_plan.spec.steps if s.id==requested['step'])
            captured={t.thing for t in step.action.targets}
            record('shared_prevalidated_before_execution',len(captured)==len(issued.issued)
                and captured<={b['thingId'] for b in (await listed())['buildings']}
                and rt.current_plan.progress[placed['step']].state=='cancelled')
            # Interrupt a real native removal after it succeeds but before Hands
            # receives the receipt. The persisted uncertain slot must not replay.
            native=rt.native
            async def lost_receipt(name,arguments,**kwargs):
                result=await native(name,arguments,**kwargs)
                if name=='home/cancel_construction':raise ConnectionError('Acceptance: lost successful cancellation receipt')
                return result
            rt.native=lost_receipt
            await rt.execute_manual_requests()
            rt.native=native
            remaining=captured & {b['thingId'] for b in (await listed())['buildings']}
            record('lost_receipt_stops_batch',len(remaining)==len(captured)-1
                and rt.current_plan.progress[requested['step']].state=='blocked')
            if args.chat:
                prompt='Cancel construction of cancel-room. Remove its remaining pending blueprints and frames. Keep completed buildings and unrelated construction orders.'
                await rt.steer(prompt)
                revision=rt.chat_revision
                async with asyncio.timeout(180):
                    while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:
                        await asyncio.sleep(.5)
                candidates=[s for s in rt.current_plan.spec.steps if s.action.kind=='cancel_construction'
                    and s.action.source_step==placed['step'] and s.id!=requested['step']]
                report['chat']={'prompt':prompt,'model':args.model,'messages':[m for m in rt.chat if m.get('revision')==revision]}
                record('local_chat_selected_remaining_cancellation',len(candidates)==1,
                    messages=report['chat']['messages'])
                retry={'step':candidates[0].id}
            else:
                retry=await command(kind='CancelConstruction',intent_id='cancel-room')
            retry_step=next(s for s in rt.current_plan.spec.steps if s.id==retry['step'])
            record('repeat_observes_remaining_exact_targets',{t.thing for t in retry_step.action.targets}==remaining)
            await rt.execute_manual_requests()
            present={b['thingId'] for b in (await listed())['buildings']}
            record('shared_cancellation_complete',not captured & present and neighbor['thingId'] in present
                and completed['thingId'] in present and rt.current_plan.progress[retry['step']].state=='complete')
            repeated=await command(kind='CancelConstruction',intent_id='cancel-room')
            await rt.execute_manual_requests()
            record('completed_repeat_has_no_native_targets',repeated['targets']==0
                and rt.current_plan.progress[repeated['step']].state=='complete')
            record('shared_manual_paused_expected_inference',rt.mode=='manual'
                and (rt.counters['model_calls']>0 if args.chat else rt.counters['model_calls']==0)
                and (await rt.game.query('home/status',colonists=False,threats=False))['time']['paused'])
            report['scope']='Native blueprint and shared semantic/Hands cancellation, including a lost successful receipt; no frame-refund or local-model acceptance'
            if args.frame:
                roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
                report['frame_workers_before']=roster
                for pawn in roster['pawns']:
                    construction=next((w for w in pawn.get('work',{}).get('types',[])
                        if 'construct' in w['name'].casefold() and w.get('disabled') is False),None)
                    if construction:
                        await command(kind='SetWorkPriority',pawn=pawn['thingId'],work_type=construction['name'],priority=1)
                        await rt.execute_manual_requests()
                facts=await rt.game.query('home/colony_facts',planning=True)
                candidates=sorted((c for c in facts['cells'] if c['walkable'] and not c['occupied']),
                    key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2)
                for cell in candidates:
                    preview=await rt.game.invoke('home/place_building',dict(defName='Bed',stuff='WoodLog',
                        x=cell['x'],z=cell['z'],rotation='north',dryRun=True))
                    if not preview.get('canPlace'):continue
                    from rimbot.shelter_handoff import safe_rotation
                    if not any(safe_rotation(row) for row in preview.get('rotations',[])):continue
                    try:
                        bed=await command(kind='PlaceBuildings',buildings={'kind':'place_buildings',
                            'placements':[{'def_name':'Bed','materials':['WoodLog'],'x':cell['x'],'z':cell['z']}]})
                    except ValueError:continue
                    report['frame_placement_preview']=preview
                    break
                else:raise AssertionError('No accepted native bed fixture')
                await rt.execute_manual_requests()
                assert rt.current_plan.progress[bed['step']].state=='waiting'
                frame=None
                for window in range(80):
                    # Normal supervised simulation with an exact native boundary;
                    # no save edits, instant construction or synthetic deliveries.
                    if rt.review_task and not rt.review_task.done():await rt.review_task
                    await rt.supervisor.change('Superfast',max_ticks=100)
                    async with asyncio.timeout(20):
                        while True:
                            state=(await rt.bridge.call('home/supervised_play',op='status')).structuredContent
                            if not state['active']:break
                            await asyncio.sleep(.1)
                    if state['stopReason']=='letter_pause' and 'Ancient danger' in state.get('stopDetail',''):
                        warning=await rt.game.query('rimworld/list_letters')
                        threats=await rt.game.query('home/status',colonists=False,threats=True)
                        counts=threats.get('counts',{})
                        record('ancient_danger_warning_observed_before_explicit_test_resume',
                            state['pauseVerified'] and counts.get('hostileCount')==0
                            and counts.get('huntingPredatorCount')==0,clock=state,letters=warning,threats=threats)
                        # This is explicit fixture control after observing the
                        # warning, never an automatic runtime danger override.
                        rt.supervisor.absorb(state)
                        rt.supervisor.allow_resume()
                    else:
                        assert state['stopReason'] in ('tick_budget','requested_pause') and state['pauseVerified'],state
                    if rt.review_task and not rt.review_task.done():await rt.review_task
                    observed=await listed()
                    current=next((b for b in observed['buildings'] if b['position']['x']==cell['x']
                        and b['position']['z']==cell['z'] and (b.get('buildDefName') or b['defName'])=='Bed'),None)
                    report['frame_progress']={'window':window,'clock':state,'target':current}
                    (args.output/'progress.json').write_text(json.dumps(report,indent=2))
                    if current and current.get('isFrame') and 0<current.get('workLeft',0)<current.get('workToBuild',0):
                        frame=current;break
                    assert current and (current.get('isBlueprint') or current.get('isFrame')),'Bed finished before partial-frame observation'
                assert frame,'No ordinarily constructed partial frame observed'
                held={r['defName']:r['have'] for r in frame['resources'] if r['have']>0}
                assert held and frame.get('materialCostUnreadable') is False
                async def stocks():
                    totals={}
                    for definition in held:
                        stock=await rt.game.query('home/list_things',match=definition,ownership='all',includeHeld=False,maxPositionsPerDef=0)
                        totals[definition]=sum(r['total'] for r in stock['things'] if r['defName']==definition)
                    return totals
                before=await stocks()
                tick=(await rt.game.query('home/status',colonists=False,threats=False))['time']['ticksGame']
                removed=await command(kind='CancelConstruction',intent_id=bed['step'])
                await rt.execute_manual_requests()
                after=await stocks()
                status=await rt.game.query('home/status',colonists=False,threats=False)
                present={b['thingId'] for b in (await listed())['buildings']}
                record('partial_frame_native_refund',frame['thingId'] not in present
                    and rt.current_plan.progress[removed['step']].state=='complete'
                    and status['time']['paused'] and status['time']['ticksGame']==tick
                    and all(after[d]-before[d]==count for d,count in held.items()),
                    frame=frame,held=held,ground_before=before,ground_after=after,tick=tick)
                report['scope']='Native blueprint/shared Hands cancellation, lost receipt, and ordinary partial-frame material refund; no local-model or mixed restart acceptance'
        coverage=['Exact native blueprint cancellation']
        if args.shared:coverage.append('shared Hands and lost-receipt recovery')
        if args.frame:coverage.append('ordinary partial-frame material refund')
        if args.chat:coverage.append('one actual local-model cancellation request')
        report['scope']=', '.join(coverage)+'. No mixed restart acceptance or general model reliability claim.'
        report['outcome']='passed'
    except Exception as error:
        report['error']=str(error)
        raise
    finally:
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--shared',action='store_true')
    parser.add_argument('--frame',action='store_true',help='With --shared, verify ordinary partial-frame construction and material refund')
    parser.add_argument('--chat',action='store_true',help='Use actual local-model chat for the fresh cancellation after a lost receipt')
    parser.add_argument('--model',default='qwen3.5-4b',help='Exact local LM Studio model for --chat')
    args=parser.parse_args()
    if args.frame and not args.shared:parser.error('--frame requires --shared')
    if args.chat and not args.shared:parser.error('--chat requires --shared')
    raise SystemExit(0 if asyncio.run(asyncio.wait_for(run(args),420)) else 1)
