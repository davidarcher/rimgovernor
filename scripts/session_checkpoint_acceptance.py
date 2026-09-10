"""Restart an isolated native game and verify paired colony/controller progress."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.headless import isolated_root, prepare_rendered
from rimgovernor.player_commands import apply_command
from rimgovernor.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart, list_checkpoints, delete_checkpoint, profile_path
from rimgovernor.store import Store
from rimgovernor.colony_plan import Decision,PlanSpec,CommitSteps,PlanStep,ColonyGoal


async def ready(rt):
    await rt.start()
    deadline=time.monotonic()+120
    while not rt.connected:
        if rt.phase=='Connection failed' or time.monotonic()>deadline:
            raise RuntimeError(str(rt.chat[-1] if rt.chat else rt.phase))
        await asyncio.sleep(.5)


async def native_event_history(rt):
    rows = []
    cursor = 0
    while True:
        batch = (await rt.bridge.call('home/supervised_play', op='events', afterCursor=cursor, limit=128)).structuredContent
        assert not batch.get('gap'), batch
        rows.extend(batch['events'])
        if batch['nextCursor'] == cursor: return rows
        cursor = batch['nextCursor']


async def prepare_native_archive(rt,*,methods=True):
    report={}
    roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
    pawn=next(p for p in roster['pawns'] if any(w['name']=='Hauling' and w.get('disabled') is False
        and w.get('priority',0)>0 for w in p['work']['types']))
    result=await apply_command(rt,{'kind':'SetWorkPriority','pawn':pawn['thingId'],'work_type':'Hauling','priority':0},
                               token=rt.context_token,revision=rt.chat_revision)
    identity=result['step']
    await rt.execute_manual_requests()
    async with asyncio.timeout(60):
        while rt.current_plan.progress[identity].state!='complete':await asyncio.sleep(.25)
    async with rt.lock:
        observed=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
        expected={p['thingId']:{w['name']:w['priority'] for w in p['work']['types']} for p in roster['pawns']}
        expected[pawn['thingId']]['Hauling']=0
        actual={p['thingId']:{w['name']:w['priority'] for w in p['work']['types']} for p in observed['pawns']}
        assert actual==expected
        plan=rt.current_plan
        original=next(s.model_dump() for s in plan.spec.steps if s.id==identity)
        progress=plan.progress[identity].model_dump()
        if methods:
            plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2,source='PLAYER',
                steps=[identity],evidence={'methods':{'native-hauling-off':[identity]}})
        spec=plan.spec.model_dump();spec['steps']=[s for s in spec['steps'] if s['id']!=identity]
        decision=Decision(expected_revision=plan.revision,disposition='revise',assessment='Archive completed work',
            rationale='Archive completed work',reply='Archive completed work',plan=PlanSpec.model_validate(spec))
    await rt.commit_strategy(decision,actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
    rt.persist()
    record=rt.store.retired_action(rt.colony,identity)
    assert record['step']==original and record['progress']==progress and identity not in plan.progress
    report['archive']={'identity':identity,'record':record,'work':expected}
    if methods:
        goal=plan.colony_goals['EnsureWorkAssignments']
        assert goal.archived_methods==1 and goal.method_seen('native-hauling-off')
        report['method_archive']=rt.store.retired_method(rt.colony,'EnsureWorkAssignments',goal.method_epoch,'native-hauling-off')
        assert report['method_archive']=={'steps':[identity]}
    return report


async def main(args):
    root=isolated_root(args.source_root,args.output/'bridge')
    if args.rendered: prepare_rendered(root)
    store=Store(args.output/'initial.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=not args.rendered)
    report={}
    try:
        await ready(rt)
        if getattr(args,'uncertain_zone',False):
            from rimgovernor.campaign_manifest import capture_manifest
            report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,
                root/('config' if args.rendered else 'config-headless'),{'mode':'no inference'})
        initial=rt.batch.summary.end_tick
        if getattr(args, 'delivery', False):
            rt.clock_task.cancel()
            await asyncio.gather(rt.clock_task, return_exceptions=True)
            clock = rt.supervisor
            started = (await rt.bridge.call('home/supervised_play', op='start', owner=clock.owner,
                speed='Normal', leaseMs=1000, injuryStopCooldownMs=0)).structuredContent
            clock.epoch = started['epoch']
            clock.record()
            await asyncio.sleep(2)
            expired = (await rt.bridge.call('home/supervised_play', op='status')).structuredContent
            assert expired['stopReason'] == 'lease_expired' and not expired['active'], expired
            original = rt.bridge.call
            dropped = False
            async def lose_events(name, **arguments):
                nonlocal dropped
                result = await original(name, **arguments)
                if name == 'home/supervised_play' and arguments.get('op') == 'events' and not dropped:
                    dropped = True
                    raise TimeoutError('Acceptance dropped an event read response')
                return result
            rt.bridge.call = lose_events
            try:
                await clock.poll()
            except TimeoutError:
                pass
            else:
                raise AssertionError('Event response was not dropped')
            rt.bridge.call = original
            rows = await clock.poll()
            assert any(row['kind'] == 'lease_expired' for row in rows), rows
            rt.receive_clock_events()
            delivered = len(rt.store.history(rt.colony, limit=10000))
            assert await clock.poll() == []
            rt.receive_clock_events()
            assert len(rt.store.history(rt.colony, limit=10000)) == delivered
            report['delivery'] = {'expired': expired, 'recovered_events': rows, 'dropped_read': dropped}
        await apply_command(rt,{'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20},
                            token=rt.context_token,revision=rt.chat_revision)
        if args.archive:
            report.update(await prepare_native_archive(rt,methods=args.methods))
        rt.reply('Checkpoint acceptance: preserve this conversation.')
        if getattr(args,'mixed',False):
            from mixed_checkpoint_fixture import prepare_mixed
            report['mixed']=await prepare_mixed(rt)
            if getattr(args,'uncertain_zone',False):
                from uncertain_checkpoint_fixture import prepare_uncertain_zone
                report['uncertain_zone']=await prepare_uncertain_zone(rt,report['mixed'])
        else:
            await rt.bridge.call('rimworld/set_time_speed',speed='Fast',ultraSpeedBoost=False)
            await asyncio.sleep(2)
        checkpoint=await create_checkpoint(rt,rt.context_token)
        assert checkpoint['tick']>=initial if getattr(args,'mixed',False) else checkpoint['tick']>initial
        report.update(checkpoint=checkpoint,initial_tick=initial,old_token=rt.context_token,
                      plan=rt.current_plan.model_dump(),chat=rt.chat)
        if getattr(args, 'durable_events', False):
            state = (await rt.bridge.call('home/supervised_play', op='status')).structuredContent
            assert state.get('durableEvents') is True
            # Ordinary start/pause transitions exceed the former 128-row ring.
            for _ in range(66):
                started = (await rt.bridge.call('home/supervised_play', op='start', owner='acceptance-journal',
                    speed='Normal', leaseMs=30000, injuryStopCooldownMs=0)).structuredContent
                if started['active']:
                    try:
                        await rt.bridge.call('home/supervised_play', op='pause', owner='acceptance-journal', epoch=started['epoch'])
                    except Exception:
                        stopped = (await rt.bridge.call('home/supervised_play', op='status')).structuredContent
                        assert stopped['epoch'] == started['epoch'] and not stopped['active'], stopped
                        report.setdefault('journal_guard_stops', []).append(stopped['stopReason'])
            report['native_events'] = await native_event_history(rt)
            assert len(report['native_events']) > 128
            # Keep the paired state current after the ordinary fixture ticks.
            checkpoint = await create_checkpoint(rt, rt.context_token)
            report.update(checkpoint=checkpoint, plan=rt.current_plan.model_dump(), chat=rt.chat)
            await rt.bridge.core('games_kill', gameId=rt.bridge.game_id)
            rt.owned_game_stopped = True
            rt.stopped = True
            rt.shutdown.set()
            # Reproduce a crash after a row flush but before publication.
            journal = profile_path(root, not args.rendered)/'RimGovernorClockEvents'
            last = sorted(journal.glob('*.xml'))[-1]
            last.rename(journal/'acceptance.pending')
            report['staged_publication'] = last.name
        else:
            await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
    except Exception as error:
        report.update(outcome='FAIL',error=str(error),plan=rt.current_plan.model_dump(),chat=rt.chat)
        (args.output/'report.json').write_text(json.dumps(report,indent=2))
        raise
    finally:
        await rt.stop();store.close()
    data,state=prepare_resume(checkpoint['manifest_path'])
    store=Store(state/'bridge.sqlite')
    resumed=BridgeRuntime(store,root,fresh=True,headless=not args.rendered,resume=checkpoint['manifest_path'])
    try:
        await ready(resumed)
        assert resumed.mode=='manual' and resumed.context_token!=report['old_token']
        assert resumed.batch.summary.end_tick in (data['tick'], data['tick']+1)
        assert resumed.current_plan.model_dump()==report['plan']
        assert resumed.chat==report['chat']
        assert not resumed.draft_owners and resumed.counters['model_calls']==0
        if getattr(args, 'durable_events', False):
            retained_events = await native_event_history(resumed)
            assert retained_events[:len(report['native_events'])] == report['native_events']
            report['durable_event_count'] = len(report['native_events'])
        if getattr(args,'mixed',False):
            if getattr(args,'uncertain_zone',False):
                from uncertain_checkpoint_fixture import verify_uncertain_zone
                report['uncertain_zone_verification']=await verify_uncertain_zone(resumed,report['uncertain_zone'])
                assert resumed.current_plan.control['costs']==report['mixed']['costs']
                assert resumed.current_plan.progress[report['mixed']['work']].state=='pending'
                assert len(resumed.current_plan.progress[report['mixed']['shell']].issued)==1
            else:
                from mixed_checkpoint_fixture import verify_mixed
                report['mixed_verification']=await verify_mixed(resumed,report['mixed'])
        if args.archive:
            archived=report['archive'];identity=archived['identity']
            assert store.retired_action(resumed.colony,identity)==archived['record']
            observed=await resumed.game.query('home/list_pawns',colonistsOnly=True,work=True)
            assert {p['thingId']:{w['name']:w['priority'] for w in p['work']['types']} for p in observed['pawns']}==archived['work']
            plan=resumed.current_plan
            try:
                plan.commit(CommitSteps(expected_revision=plan.revision,reason='Replay rejection acceptance',
                    steps=[PlanStep.model_validate(archived['record']['step'])]).decision(plan),actor='strategist',tick=data['tick'])
            except ValueError as error:
                assert 'Retired action identity' in str(error)
            else:raise AssertionError('Archived action identity was admitted again')
            assert resumed.counters['actions']==0
            if args.methods:
                goal=plan.colony_goals['EnsureWorkAssignments']
                assert goal.archived_methods==1 and goal.evidence['methods']=={}
                assert goal.method_seen('native-hauling-off') and not goal.method_seen('never-issued')
                assert store.retired_method(resumed.colony,'EnsureWorkAssignments',goal.method_epoch,'native-hauling-off')==report['method_archive']
        report.update(resumed_tick=resumed.batch.summary.end_tick,new_token=resumed.context_token)
        if getattr(args, 'retention', False):
            retained = list_checkpoints(root)
            assert any(row['manifest_path'] == checkpoint['manifest_path'] and row['valid'] for row in retained)
            try:
                await delete_checkpoint(resumed, resumed.context_token, checkpoint['manifest_path'])
            except ValueError as error:
                assert 'active resume' in str(error)
            else:
                raise AssertionError('Active checkpoint was deleted')
            disposable = await create_checkpoint(resumed, resumed.context_token)
            await delete_checkpoint(resumed, resumed.context_token, disposable['manifest_path'])
            assert not Path(disposable['manifest_path']).exists()
            assert list_checkpoints(root) == retained
            report['retention'] = {'retained': retained, 'deleted': disposable}
        if getattr(args,'rewind',False):
            from mixed_checkpoint_fixture import verify_rewind
            report['rewind']=await verify_rewind(resumed,report['mixed'])
        report['outcome']='PASS'
        print('PASS: native tick, identity, PLAYER goal, policy and conversation preserved; new load in Manual',flush=True)
    finally:
        await resumed.stop();store.close()
        (args.output/'report.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--timeout',type=int,help='Whole-probe wall budget in seconds; defaults to 240 (600 for durable events)')
    parser.add_argument('--rendered',action='store_true',help='Verify the visible private profile instead of headless mode')
    parser.add_argument('--retention', action='store_true', help='Verify retained pairs, active-resume protection and exact deletion after native restart')
    parser.add_argument('--delivery', action='store_true', help='Verify native lease expiry and event recovery after a lost read response')
    parser.add_argument('--durable-events', action='store_true', help='Exceed 128 native events, kill the owned game and verify exact journal retention after paired restart')
    parser.add_argument('--archive',action='store_true',help='Retire a completed native work assignment and verify its archive and no replay after restart')
    parser.add_argument('--methods',action='store_true',help='With --archive, also verify durable method deduplication after native paired restart')
    parser.add_argument('--mixed',action='store_true',help='Preserve partial native shell, unissued reservations, pending growing zone and work assignment without replay')
    parser.add_argument('--rewind',action='store_true',help='With --mixed, reload the original native save and verify obsolete queued work and writes cannot replay')
    parser.add_argument('--uncertain-zone',action='store_true',help='With --mixed, lose a successful native zone-create receipt and preserve the uncertain action across paired restart')
    args=parser.parse_args()
    if args.timeout is not None and args.timeout <= 0:parser.error('--timeout must be positive')
    if args.methods and not args.archive:parser.error('--methods requires --archive')
    if args.rewind and not args.mixed:parser.error('--rewind requires --mixed')
    if args.uncertain_zone and (not args.mixed or args.rewind):parser.error('--uncertain-zone requires --mixed and excludes --rewind')
    asyncio.run(asyncio.wait_for(main(args),args.timeout or (600 if args.durable_events else 240)))
