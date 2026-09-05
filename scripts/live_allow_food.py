"""Bounded real-model regression: propose, approve, allow observed meals, read back.

Requires --execute and an idle Manual dashboard. Leaves selected meals allowed.
This isolates the food handoff; it is not an autonomous-startup test.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path
import httpx
from rimbot.config import Settings
from rimbot.contracts import Proposal
from rimbot.runtime import Runtime
from rimbot.store import Store

async def run():
    async with httpx.AsyncClient() as c:
        state=(await c.get('http://127.0.0.1:8787/api/state')).json()
    if state['mode']!='manual' or state['busy']:raise RuntimeError('Dashboard must be idle in Manual.')
    folder=Path('.rimbot/allow-tests')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings(**state['settings']))
    report={'passed':False};start=time.monotonic()
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        mid=rt.observation['map']['id']
        rows=await rt.api.call('get_map_things',{'map_id':mid})
        meals=[r for r in rows if 'FoodMeals' in r.get('categories',[]) and r.get('is_forbidden')]
        if not meals:raise RuntimeError('No forbidden meal fixture.')
        anchor=max(meals,key=lambda r:r['stack_count'])['position']
        meals=[r for r in meals if sum((r['position'][k]-anchor[k])**2 for k in ('x','z'))<=100]
        ids={r['thing_id'] for r in meals};report['before']=meals
        context={'player_direction':'Allow only the supplied meal stacks so colonists can eat them. This test needs only the permission flag changed, not hauling, beds or other colony work. Inspect and propose the native order, then verify those same items.',
                 'selected_map':rt.observation['map'],'selected_meals':meals,'capabilities':rt.catalog.listing()}
        proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Survival']),120)
        report.update(proposals=proposals,decision=decision.model_dump())
        actions=[(r,a) for r in decision.accepted for a in Proposal.model_validate(proposals[r]).actions]
        if not actions:raise RuntimeError('No approved food action.')
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':1})
        for role,a in actions:
            if a.endpoint!='post_things_set_forbidden' or a.arguments.get('forbidden') is not False or not set(a.arguments.get('thing_ids',[]))<=ids:
                raise RuntimeError('Action exceeded selected food fixture.')
            await rt.execute(a,role)
        after=await rt.api.call('get_map_things',{'map_id':mid})
        report['after']=[r for r in after if r['thing_id'] in ids]
        report['passed']=len(report['after'])==len(ids) and all(not r['is_forbidden'] for r in report['after'])
        if not report['passed']:raise RuntimeError('Selected meals were not all allowed.')
    except BaseException as e:
        report['error']=str(e);raise
    finally:
        if rt.observation.get('game'):
            game=await rt.api.call('get_game_state',{},fresh=True)
            if game.get('session_id')==rt.observation['game']['session_id']:
                await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        report.update(seconds=round(time.monotonic()-start,2),counters=rt.counters)
        (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(f"Passed: {report['passed']}. Evidence: {folder}/report.json",flush=True)
        await rt.stop();rt.store.close()

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--execute',action='store_true',required=True);p.parse_args()
    asyncio.run(run())
