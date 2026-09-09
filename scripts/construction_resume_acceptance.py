"""Finish interrupted-cancellation acceptance from an immutable native checkpoint."""
import argparse
import asyncio
import json
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.session_checkpoint import prepare_resume
from rimbot.store import Store


async def run(args):
    source=json.loads(args.source_report.read_text())
    checkpoint=source['checkpoint']['manifest_path']
    data,state=prepare_resume(checkpoint)
    args.output.mkdir(parents=True,exist_ok=False)
    store=Store(state/'bridge.sqlite')
    rt=BridgeRuntime(store,Path(data['root']),fresh=True,headless=True,resume=checkpoint,
        settings=Settings(model=args.model,timeout_seconds=90))
    report={'outcome':'failed','source_report':str(args.source_report.resolve()),'checkpoint':checkpoint,'cases':[]}
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def listed():
        value=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        assert value.get('success') and not value.get('skipped',{}).get('byMaxDetailed')
        return {b['thingId'] for b in value['buildings']}
    async def chat(prompt):
        await rt.steer(prompt)
        revision=rt.chat_revision
        async with asyncio.timeout(180):
            while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:
                await asyncio.sleep(.5)
        await rt.execute_manual_requests()
        report.setdefault('chat',[]).append({'prompt':prompt,'messages':[m for m in rt.chat if m.get('revision')==revision]})
    try:
        await ready(rt)
        plan=rt.current_plan
        cancelled=next(s for s in plan.spec.steps if s.action.kind=='cancel_construction'
            and any(v.get('confirmed') is False for v in plan.progress[s.id].issued.values()))
        before=await listed()
        remaining={t.thing for t in cancelled.action.targets}&before
        record('paired_load_keeps_partial_cancellation_and_unrelated_orders',rt.mode=='manual'
            and cancelled.action.loadToken!=rt.identity['loadToken'] and len(remaining)==1
            and rt.counters['actions']==0 and plan.model_dump()==source['plan'],remaining=sorted(remaining),tick=rt.batch.summary.end_tick)
        await chat('Explain which construction is still pending. This is an inspection request only; issue no game orders.')
        record('inspection_changes_direction_without_native_orders',await listed()==before and rt.counters['actions']==0)
        # Deliberately queue the saved old action in the fresh context to exercise
        # its load guard, beyond the normal Manual queue being cleared on restart.
        progress=plan.progress[cancelled.id]
        progress.state='pending'
        rt.manual_requests.append((cancelled.id,rt.context_token,rt.chat_revision))
        await rt.execute_manual_requests()
        record('old_load_cancellation_is_refused',progress.state=='blocked'
            and progress.failure.code=='cancellation_context_changed' and await listed()==before
            and rt.counters['actions']==0,failure=progress.failure.model_dump())
        await chat('Cancel construction of '+cancelled.action.source_step+
            '. Remove its remaining pending blueprints and frames. Preserve unrelated construction and completed buildings.')
        after=await listed()
        record('fresh_local_request_removes_only_remaining_targets',before-after==remaining and not after-before
            and rt.mode=='manual' and (await rt.game.query('home/status',colonists=False,threats=False))['time']['paused'],
            before=sorted(before),after=sorted(after))
        report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error)
        raise
    finally:
        report['plan']=rt.current_plan.model_dump()
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-report',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default='qwen3.5-4b')
    asyncio.run(asyncio.wait_for(run(parser.parse_args()),500))
