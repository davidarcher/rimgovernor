"""Inspect deterministic hunting candidates in a visible disposable colony."""
import argparse
import asyncio
import json
import socket
from pathlib import Path
import uvicorn
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_server import create_app
from rimbot.headless import isolated_root,prepare_rendered
from rimbot.hunting import screen_prey
from rimbot.store import Store


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
        report.update(mode=rt.mode,paused=status['time']['paused'],counters=rt.counters)
        if rt.mode=='manual' and report['paused'] and rt.counters['actions']==rt.counters['model_calls']==0:
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
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
