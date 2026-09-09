"""Visible native acceptance of retained cancelled work and unrelated player orders."""
import argparse
import asyncio
from copy import deepcopy
import json
from pathlib import Path
import shutil
import socket
import uvicorn
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_server import create_app
from rimbot.headless import isolated_root,prepare_rendered
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import create_checkpoint,prepare_resume,read_checkpoint,stop_for_restart
from rimbot.store import Store


async def run(args):
    checkpoint=read_checkpoint(args.checkpoint)
    with socket.socket() as listener: listener.bind(('127.0.0.1',args.port))
    root=isolated_root(Path(checkpoint['root']),args.output/'bridge')
    prepare_rendered(root)
    shutil.copy2(args.checkpoint.parent/'game.rws',root/'profile/Saves/RimBot-tribal8-baseline.rws')
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=False)
    server=uvicorn.Server(uvicorn.Config(create_app(rt),host='127.0.0.1',port=args.port,log_level='warning'))
    report={'outcome':'failed','checkpoint':str(args.checkpoint),'cases':[]}
    task=asyncio.create_task(server.serve())
    async def ready():
        async with asyncio.timeout(120):
            while not rt.connected:
                if rt.phase=='Connection failed': raise ValueError(str(rt.chat[-1:]))
                await asyncio.sleep(1)
    async def command(payload):
        async with asyncio.timeout(120):
            result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
            await rt.execute_manual_requests()
            return result
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence))
        print(json.dumps(dict(name=name,passed=bool(passed))),flush=True)
        (args.output/'progress.json').write_text(json.dumps(report,indent=2))
        if not passed: raise AssertionError(name)
    async def buildings(room):
        result=await rt.game.query('home/list_buildings',x=room['x']+room['width']//2,
            z=room['z']+room['height']//2,radius=max(room['width'],room['height']),
            aggregate=False,playerOnly=True)
        if result.get('skipped',{}).get('byMaxDetailed'): raise ValueError('Native building observation was truncated')
        return sorted((b for b in result['buildings'] if b.get('isBlueprint')
            and room['x']<=b['position']['x']<room['x']+room['width']
            and room['z']<=b['position']['z']<room['z']+room['height']),key=lambda b:b['thingId'])
    try:
        await ready()
        report['session_id']=rt.context_token
        facts=await rt.game.query('home/colony_facts',planning=True)
        layout=await rt.controller.skills.layout(facts)
        shell=rt.controller.skills.shell(layout)
        before=await buildings(shell['bounds'])
        await command({'kind':'BuildRoom','intent_id':'cancel-test-room','room':shell,'purpose':'shelter'})
        identity='player-cancel-test-room'
        progress=rt.current_plan.progress[identity]
        issued=await buildings(shell['bounds'])
        record('native_room_orders_issued',progress.state=='waiting' and bool(progress.issued)
            and len(issued)-len(before)==len(progress.issued),
            state=progress.state,receipts=progress.issued,native_before=before,native_issued=issued)
        await command({'kind':'CancelGoal','goal':'intent-cancel-test-room'})
        retained=deepcopy(rt.current_plan.progress[identity])
        record('cancellation_retains_native_orders',retained.state=='cancelled' and await buildings(shell['bounds'])==issued)
        if args.restart:
            rt.reply('Cancelled room orders must survive the paired restart.')
            checkpoint=await create_checkpoint(rt,rt.context_token)
            saved_plan=rt.current_plan.model_dump()
            saved_chat=deepcopy(rt.chat)
            old_token=rt.context_token
            report['restart_checkpoint']=checkpoint
            report['before_restart_counters']=dict(rt.counters)
            print('Restarting the disposable colony from its paired checkpoint.',flush=True)
            await stop_for_restart(rt,old_token,checkpoint['manifest_path'])
            server.should_exit=True
            await task
            store.close()
            data,state=prepare_resume(checkpoint['manifest_path'])
            store=Store(state/'bridge.sqlite')
            rt=BridgeRuntime(store,root,fresh=True,headless=False,resume=checkpoint['manifest_path'])
            server=uvicorn.Server(uvicorn.Config(create_app(rt),host='127.0.0.1',port=args.port,log_level='warning'))
            task=asyncio.create_task(server.serve())
            await ready()
            record('paired_restart_preserves_cancelled_plan',rt.context_token!=old_token
                and rt.current_plan.model_dump()==saved_plan and rt.chat==saved_chat
                and rt.batch.summary.end_tick in (data['tick'],data['tick']+1)
                and not rt.manual_requests and not rt.draft_owners,
                old_token=old_token,new_token=rt.context_token,saved_tick=data['tick'],loaded_tick=rt.batch.summary.end_tick)
            record('native_blueprints_survive_restart',await buildings(shell['bounds'])==issued)
            restored=rt.current_plan.model_dump()
            result=await command({'kind':'BuildRoom','intent_id':'cancel-test-room','room':shell,'purpose':'shelter'})
            record('repeated_intent_remains_cancelled',result=={'existing_step':identity,'state':'cancelled'}
                and rt.current_plan.model_dump()==restored and rt.counters['actions']==0)
        catalog=await rt.game.invoke('home/research',{})
        project=next(p for p in catalog['available'] if p['defName']!=(catalog.get('current') or {}).get('defName'))
        await command({'kind':'SetResearch','project':project['label']})
        selected=await rt.game.invoke('home/research',{})
        steps=[s for s in rt.current_plan.spec.steps if s.action.kind=='native_operation' and s.action.tool=='home/research']
        record('unrelated_player_research_completes',bool(steps) and selected['current']['defName']==project['defName']
            and all(s.source=='PLAYER' and rt.current_plan.progress[s.id].state=='complete' for s in steps),
            requested=project,observed=selected.get('current'))
        record('cancelled_work_unchanged',rt.current_plan.progress[identity]==retained
            and await buildings(shell['bounds'])==issued and rt.current_plan.colony_goals['intent-cancel-test-room'].cancelled)
        status=await rt.game.query('home/status',colonists=False,threats=False)
        record('manual_paused_without_inference',rt.mode=='manual' and status['time']['paused'] and rt.counters['model_calls']==0)
        report['outcome']='passed'
    except Exception as error: report['error']=repr(error)
    finally:
        try:
            if rt.connected and rt.bridge:
                async with rt.lock:
                    await rt.halt()
                    await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)
                    rt.owned_game_stopped=True
        except Exception as error: report['cleanup_error']=repr(error)
        server.should_exit=True
        await task
        report.update(plan=rt.current_plan.model_dump(),counters=rt.counters)
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        store.close()
    print(json.dumps({k:v for k,v in report.items() if k not in ('plan','cases')}),flush=True)
    return report['outcome']=='passed' and 'cleanup_error' not in report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--port',type=int,default=8788)
    parser.add_argument('--restart',action='store_true',help='Restart the owned game from a paired checkpoint after cancellation')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
