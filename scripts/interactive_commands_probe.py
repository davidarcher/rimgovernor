"""Native player-chat acceptance in an isolated, paused colony."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.headless import isolated_root,prepare
from rimbot.store import Store


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge')
    prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,settings=Settings(timeout_seconds=90))
    report={'outcome':'failed','cases':[]}
    async def chat(prompt):
        await rt.steer(prompt)
        revision=rt.chat_revision
        async with asyncio.timeout(180):
            while (rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating):
                await asyncio.sleep(.5)
        return [m['text'] for m in rt.chat if m.get('revision')==revision and m.get('kind')=='summary']
    try:
        await rt.start()
        async with asyncio.timeout(120):
            while not rt.connected: await asyncio.sleep(1)
        roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
        pawn=next(p for p in roster['pawns'] if any(w['name']=='Hauling' and w.get('disabled') is False
                  and w.get('priority',0)>0 for w in p['work']['types']))
        reply=await chat('Turn off hauling for '+pawn['name']+'. Do not change other work assignments.')
        observed=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
        after=next(p for p in observed['pawns'] if p['thingId']==pawn['thingId'])
        report['cases'].append({'command':'work','reply':reply,'passed':any(w['name']=='Hauling' and w['priority']==0
                                for w in after['work']['types'])})
        reply=await chat('Get us to 20 days of food.')
        goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
        report['cases'].append({'command':'goal','reply':reply,'passed':bool(goal and goal.source=='PLAYER'
                                and goal.target.get('food_days')==20)})
        reply=await chat("Stop spending components unless they're needed for defense.")
        policy=rt.current_plan.control.get('resource_policy',{}).get('ComponentIndustrial',{})
        report['cases'].append({'command':'policy','reply':reply,'passed':policy.get('spending')=='defense_only'})
        status=await rt.game.query('home/status',colonists=False,threats=False)
        report.update(mode=rt.mode,paused=status['time']['paused'],actions=rt.counters['actions'])
        if all(r['passed'] for r in report['cases']) and rt.mode=='manual' and report['paused']:
            report['outcome']='passed'
    except Exception as error:
        report['error']=str(error)
    finally:
        try:
            if rt.connected and rt.bridge:
                async with rt.lock:
                    await rt.halt()
                    await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)
        except Exception as error: report['cleanup_error']=str(error)
        await rt.stop()
        report.update(plan=rt.current_plan.model_dump(),counters=rt.counters)
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        store.close()
    print(json.dumps({k:v for k,v in report.items() if k!='plan'}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
