"""Inspect deterministic hunting candidates in a visible disposable colony."""
import argparse
import asyncio
import json
import socket
from pathlib import Path
import uvicorn
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge_server import create_app
from rimgovernor.headless import isolated_root,prepare_rendered
from rimgovernor.hunting import screen_prey
from rimgovernor.store import Store
from rimgovernor.colony_plan import ColonyGoal,CommitSteps
from rimgovernor.colony_skills import native
from rimgovernor.config import ModelRole


async def run(args):
    with socket.socket() as listener: listener.bind(('127.0.0.1',args.port))
    root=isolated_root(args.source_root,args.output/'bridge')
    prepare_rendered(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=False)
    server=uvicorn.Server(uvicorn.Config(create_app(rt),host='127.0.0.1',port=args.port,log_level='warning'))
    task=asyncio.create_task(server.serve())
    report={'outcome':'failed','samples':[]}
    try:
        async with asyncio.timeout(120):
            while not rt.connected:
                if rt.phase=='Connection failed': raise ValueError(str(rt.chat[-1:]))
                await asyncio.sleep(1)
        for _ in range(5):
            facts=await rt.game.query('home/colony_facts',planning=True)
            wildlife=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
            if not wildlife.get('pawns'): raise ValueError('No native wildlife in fixture')
            candidates,evidence=screen_prey(wildlife['pawns'],facts['center'])
            report['samples'].append({'anchor':facts['center'],'wildlife':wildlife['pawns'],'screen':evidence})
            print(json.dumps({'candidates':len(candidates),'rejected':len(evidence['rejected'])}),flush=True)
            rt.note('hunting_screen_probe','Native wildlife screened without issuing hunting orders',**evidence)
            await asyncio.sleep(4)
        status=await rt.game.query('home/status',colonists=False,threats=False)
        if args.dispatch:
            if not candidates: raise ValueError('No eligible native prey for dispatch acceptance')
            target=candidates[0]
            rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2,source='PLAYER')
            designator=await rt.controller.skills.designator('Designator_Hunt')
            steps,_=rt.controller.skills.steps('EnsureFoodSupply','hunt-'+target['thingId'],[
                native('rimworld/apply_architect_designator',designatorId=designator,
                       x=target['position']['x'],z=target['position']['z'],keepSelected=False)],facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Native hunting acceptance',steps=steps).decision(rt.current_plan),
                actor=ModelRole.STRATEGIST,expected_token=rt.context_token,expected_revision=rt.chat_revision)
            rt.manual_requests=[(steps[0].id,rt.context_token,rt.chat_revision)]
            await rt.execute_manual_requests()
            observed=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
            selected=next(p for p in observed['pawns'] if p['thingId']==target['thingId'])
            report['dispatch']={'target':target['thingId'],'progress':rt.current_plan.progress[steps[0].id].model_dump(),
                                'observed':selected}
            if not selected['animals']['designations']['hunt'] or rt.current_plan.progress[steps[0].id].state!='complete':
                raise ValueError('Native hunting designation was not confirmed')
        report.update(mode=rt.mode,paused=status['time']['paused'],counters=rt.counters)
        if rt.mode=='manual' and report['paused'] and rt.counters['actions']==int(args.dispatch) and rt.counters['model_calls']==0:
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
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        store.close()
    print(report['outcome'],flush=True)
    return report['outcome']=='passed' and 'cleanup_error' not in report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--port',type=int,default=8788)
    parser.add_argument('--dispatch',action='store_true',help='Issue and verify one exact-prey designation through the shared compiler and Hands')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
