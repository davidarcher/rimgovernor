"""Ordinary pawn construction followed by deterministic furnishing of a player shell."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root,prepare
from rimbot.player_commands import apply_command
from rimbot.store import Store
from rimbot.colony_plan import ColonyGoal,CommitSteps
from rimbot.colony_policy import starter_layouts
from rimbot.campaign_manifest import capture_manifest


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');configuration=prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','history':[],'scope':'Native player shell construction and sleeping handoff; no survival claim'}
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            root,configuration,rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt)
        facts=await rt.game.query('home/colony_facts',planning=True)
        # Explicit fixture setup uses the production supply method and Hands.
        # Native stocks and saves are never synthesized or edited.
        while facts.get('forbiddenSupplies'):
            goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
            method,actions=await rt.controller.skills.compile('AllowStartingSupplies',facts,[])
            steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Allow starting supplies for the player room',steps=steps).decision(rt.current_plan),
                actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
            for _ in steps:
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                    if rt.current_plan.progress[s.id].state=='pending')
                await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
            facts=await rt.game.query('home/colony_facts',planning=True)
        layout=await rt.controller.skills.layout(facts)
        shell=rt.controller.skills.shell(layout)
        if args.services:
            # A player asks for another observed site; keep the old cached layout
            # so a stale-coordinate service method cannot accidentally pass.
            preference=dict(facts,center={'x':facts['center']['x']+16,'z':facts['center']['z']+12})
            alternatives=starter_layouts(preference)
            selected=next(candidate for candidate in alternatives
                if abs(candidate['room']['x']-layout['room']['x'])>=9 or abs(candidate['room']['z']-layout['room']['z'])>=9)
            shell=rt.controller.skills.shell(selected)
            report['cached_starter_room']=layout['room']
        result=await apply_command(rt,{'kind':'BuildRoom','intent_id':'handoff-home','room':shell,'purpose':'shelter'},
                                   token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        report.update(shell=shell,player_step=result['step'],initial_tick=rt.batch.summary.end_tick)
        if args.restart_pending:
            from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
            from rimbot.bridge import runtime_file_read
            roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
            assignments = [(p['thingId'], next(w['priorityStored'] if p['work'].get('manualPriorities') else
                int(w['priority'] > 0) for w in p['work']['types'] if w['name'] == 'Construction'))
                for p in roster['pawns'] if any(w['name'] == 'Construction' and not w['disabled'] for w in p['work']['types'])]
            for pawn, priority in assignments:
                if priority == 0: continue
                await apply_command(rt, dict(kind='SetWorkPriority', pawn=pawn, work_type='Construction', priority=0),
                    token=rt.context_token, revision=rt.chat_revision)
                await rt.execute_manual_requests()
            if rt.review_task and not rt.review_task.done(): await rt.review_task
            await rt.supervisor.change('Superfast', max_ticks=600)
            async with asyncio.timeout(60):
                while True:
                    clock = (await runtime_file_read(rt.bridge.call, 'home/supervised_play', op='status')).structuredContent
                    if not clock['active']: break
                    await asyncio.sleep(.2)
            assert clock['pauseVerified'] and clock['stopReason'] == 'tick_budget', clock
            async with rt.lock:
                await rt.refresh_clock_events()
            if rt.review_task and not rt.review_task.done(): await rt.review_task
            before_buildings = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
            checkpoint = await create_checkpoint(rt, rt.context_token)
            expected = rt.current_plan.model_dump()
            pending = expected['progress'][result['step']]
            assert pending['state'] == 'waiting' and pending['issued']
            old_token = rt.context_token
            await stop_for_restart(rt, old_token, checkpoint['manifest_path']); await rt.stop(); store.close()
            data, state = prepare_resume(checkpoint['manifest_path'])
            store = Store(state/'bridge.sqlite')
            rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'], model_factory=lambda _:NoInference())
            await ready(rt)
            after_buildings = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
            assert rt.mode == 'manual' and rt.context_token != old_token and rt.current_plan.model_dump() == expected
            assert rt.counters['actions'] == 0 and rt.batch.summary.end_tick in (data['tick'], data['tick']+1)
            assert {b['thingId'] for b in before_buildings['buildings']} == {b['thingId'] for b in after_buildings['buildings']}
            report['pending_restart'] = dict(checkpoint=checkpoint, plan=expected, clock=clock, buildings=after_buildings,
                original_construction_assignments=assignments)
            print('PASS: delayed shell, native identities and receipts preserved across held restart', flush=True)
            for pawn, priority in assignments:
                if priority == 0: continue
                await apply_command(rt, dict(kind='SetWorkPriority', pawn=pawn, work_type='Construction', priority=priority),
                    token=rt.context_token, revision=rt.chat_revision)
                await rt.execute_manual_requests()
        rt.current_plan.control.setdefault('policy',{})['execution_speed']='Superfast'
        await rt.set_mode('automate')
        deadline=time.monotonic()+args.seconds
        while time.monotonic()<deadline:
            await asyncio.sleep(5)
            control=rt.current_plan.control
            row={'tick':rt.clock.get('ticksGame'),'mode':rt.mode,'phase':rt.phase,
                 'shelter':rt.current_plan.colony_goals.get('EnsureInitialShelter').model_dump()
                    if 'EnsureInitialShelter' in rt.current_plan.colony_goals else None,
                 'player_state':rt.current_plan.progress[result['step']].state,
                 'indoorSleepingCapacity':control.get('facts',{}).get('indoorSleepingCapacity'),
                 'adopted':control.get('adopted_shelter')}
            report['history'].append(row)
            (args.output/'progress.json').write_text(json.dumps(report,indent=2))
            print(json.dumps(row),flush=True)
            assert NoInference.attempts==0
            assert not any(s.action.kind=='build_room_shell' and s.source=='AUTOPILOT' for s in rt.current_plan.spec.steps)
            blockers={name:goal.reason for name,goal in rt.current_plan.colony_goals.items()
                if name in ('EnsureInitialShelter','EnsureCooking','EnsureFoodStorage') and goal.status=='blocked'
                and not goal.reason.startswith('Resources:')}
            if blockers:
                report['blockers']=blockers
                break
            if (row['player_state']=='complete' and row['adopted'] and row['adopted']['intent']=='intent-handoff-home'
                    and (row['indoorSleepingCapacity'] or 0)>=facts['colonists']
                    and (not args.services or (control.get('criteria',{}).get('cooking') and control.get('criteria',{}).get('storage')))):
                await rt.set_mode('manual')
                rooms=await rt.game.query('home/list_rooms',cells=True,x=shell['bounds']['x']+1,z=shell['bounds']['z']+1)
                adopted=next(room for room in rooms['rooms'] if room['id']==row['adopted']['room_id'])
                assert adopted['properRoom'] and adopted['openRoofCount']==0 and len(adopted['beds'])>=facts['colonists']
                if args.services:
                    assert adopted['stockpileCellsInRoom']>=9
                    assert any(item['defName']=='Campfire' and item['count']>=1 for item in adopted['contents'])
                report.update(outcome='passed',rooms=rooms)
                break
        if report['outcome']!='passed':report['error']='Native handoff blocked or did not complete within the bounded trial'
    except Exception as error:
        report['error']=str(error)
        raise
    finally:
        report.update(plan=rt.current_plan.model_dump(),model_calls=rt.counters['model_calls'],model_attempts=NoInference.attempts)
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=600)
    parser.add_argument('--services',action='store_true',help='Use a different player site and require native indoor cooking and food storage there')
    parser.add_argument('--restart-pending',action='store_true',help='Disable ordinary construction work for 600 ticks, verify a held paired restart, then restore work and require actual shell/furnishing completion')
    args=parser.parse_args()
    raise SystemExit(0 if asyncio.run(asyncio.wait_for(run(args),args.seconds+240)) else 1)
