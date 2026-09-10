"""Restart an isolated native game and verify paired colony/controller progress."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root, prepare_rendered
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.store import Store
from rimbot.colony_plan import Decision,PlanSpec,CommitSteps,PlanStep,ColonyGoal


async def ready(rt):
    await rt.start()
    deadline=time.monotonic()+120
    while not rt.connected:
        if rt.phase=='Connection failed' or time.monotonic()>deadline:
            raise RuntimeError(str(rt.chat[-1] if rt.chat else rt.phase))
        await asyncio.sleep(.5)


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
        initial=rt.batch.summary.end_tick
        await apply_command(rt,{'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20},
                            token=rt.context_token,revision=rt.chat_revision)
        if args.archive:
            report.update(await prepare_native_archive(rt,methods=args.methods))
        rt.reply('Checkpoint acceptance: preserve this conversation.')
        if getattr(args,'mixed',False):
            from mixed_checkpoint_fixture import prepare_mixed
            report['mixed']=await prepare_mixed(rt)
        else:
            await rt.bridge.call('rimworld/set_time_speed',speed='Fast',ultraSpeedBoost=False)
            await asyncio.sleep(2)
        checkpoint=await create_checkpoint(rt,rt.context_token)
        assert checkpoint['tick']>=initial if getattr(args,'mixed',False) else checkpoint['tick']>initial
        report.update(checkpoint=checkpoint,initial_tick=initial,old_token=rt.context_token,
                      plan=rt.current_plan.model_dump(),chat=rt.chat)
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
        if getattr(args,'mixed',False):
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
    parser.add_argument('--rendered',action='store_true',help='Verify the visible private profile instead of headless mode')
    parser.add_argument('--archive',action='store_true',help='Retire a completed native work assignment and verify its archive and no replay after restart')
    parser.add_argument('--methods',action='store_true',help='With --archive, also verify durable method deduplication after native paired restart')
    parser.add_argument('--mixed',action='store_true',help='Preserve partial native shell, unissued reservations, pending growing zone and work assignment without replay')
    parser.add_argument('--rewind',action='store_true',help='With --mixed, reload the original native save and verify obsolete queued work and writes cannot replay')
    args=parser.parse_args()
    if args.methods and not args.archive:parser.error('--methods requires --archive')
    if args.rewind and not args.mixed:parser.error('--rewind requires --mixed')
    asyncio.run(asyncio.wait_for(main(args),240))
