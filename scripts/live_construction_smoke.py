"""Real-model bed-blueprint milestone. Leaves the observed blueprint in the test game.

Run --execute with dashboard Manual and an unpaused disposable colony loaded.
The test supplies a narrow player objective, live definitions and a checked site;
managers must produce the blueprint payload, checks and administrator decision.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path
import httpx
from rimbot.config import Settings
from rimbot.contracts import Proposal
from rimbot.planner import ROLES
from rimbot.runtime import Runtime
from rimbot.store import Store


async def run(timeout):
    async with httpx.AsyncClient() as client:
        state=(await client.get('http://127.0.0.1:8787/api/state')).json()
    if state['mode']!='manual' or state['busy']:raise RuntimeError('Set dashboard to Manual first.')
    folder=Path('.rimbot/construction-tests')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings(**state['settings']))
    report={'passed':False,'model':rt.settings.model};started=time.monotonic()
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        mid=rt.observation['map']['id']
        defs=(await rt.api.call('get_def_all',{}))['things_defs']
        # These labels are the explicit test objective, not production material policy.
        bed=next(d for d in defs if d.get('label')=='bed' and d.get('category')=='Building')
        wood=next(d for d in defs if d.get('label')=='wood' and d.get('category')=='Item')
        pawn=rt.observation['pawns'][0]['colonist'];base=pawn['position'];site=None
        async def at(pos):return await rt.api.call('get_map_things_at',{'map_id':mid,'position':pos})
        candidates=sorted(((x,z) for x in range(-8,9) for z in range(-8,9) if 2<=abs(x)+abs(z)<=10),key=lambda p:abs(p[0])+abs(p[1]))
        for dx,dz in candidates:
            center={'x':base['x']+dx,'y':0,'z':base['z']+dz}
            a=center;b={**center,'z':center['z']+1}
            check=await rt.api.call('post_builder_check_zone',{'map_id':mid,'point_a':a,'point_b':b},write=False)
            if not check.get('can_build'):continue
            cells=[{'x':x,'y':0,'z':z} for x in range(a['x'],b['x']+1) for z in range(a['z'],b['z']+1)]
            empty=True
            for cell in cells:
                if await at(cell):empty=False;break
            if empty:site=center;break
        if site is None:raise RuntimeError('No clear nearby test footprint found; no orders sent.')
        report.update(site=site,map_id=mid,definitions=[bed,wood],site_check=check)
        context={'player_direction':f"Construction integration test only: place exactly ONE north-facing (rotation 0) wooden bed BLUEPRINT at {site}, map {mid}. Infrastructure owns placement. Other managers should submit actions: []. Do not construct shelter, floors, or issue pawn orders. Milestone completion means the blueprint appears at that exact cell, NOT a finished bed. Leave resources and existing structures alone.",
            'observed_definitions':[bed,wood],'site':site,'map_id':mid,'site_check':check,'things_at_site':[],
            'relevant_contracts':[rt.catalog.get(n) for n in ('post_builder_blueprint','get_map_things_at','post_builder_check_zone')],
            'observation_notes':'The north-facing bed footprint at the specified site was clear. Exact-cell things query returns a list wrapped as items/total by query. Before placement total is 0. Blueprint placement can be verified with the same exact-cell query. Recheck the site before placing.'}
        async def cycle():
            proposals,decision=await rt.coordinate(context,list(ROLES))
            report.update(proposals=proposals,decision=decision.model_dump())
            if set(proposals)!=set(ROLES):raise RuntimeError('Missing manager proposal.')
            actions=[(r,a) for r in decision.accepted for a in Proposal.model_validate(proposals[r]).actions]
            if len(actions)!=1:raise RuntimeError(f'Expected one approved construction action, got {len(actions)}.')
            role,action=actions[0];args=action.arguments;blueprint=args.get('blueprint',{});buildings=blueprint.get('buildings',[])
            if role!='Infrastructure' or action.endpoint!='post_builder_blueprint' or args.get('map_id')!=mid or args.get('position')!=site or args.get('clear_obstacles') or blueprint.get('floors') or len(buildings)!=1:
                raise RuntimeError('Proposal does not match the bounded construction test.')
            building=buildings[0]
            if building.get('def_name')!=bed['def_name'] or building.get('stuff_def_name')!=wood['def_name'] or building.get('rel_x',0)!=0 or building.get('rel_z',0)!=0 or building.get('rotation',0)!=0:
                raise RuntimeError('Wrong building, material or relative offset.')
            if await at(site) or await rt.check(action.done):raise RuntimeError('Site changed or completion check already true; nothing sent.')
            await rt.execute(action,role)
            after=await at(site);report.update(after=after,work=rt.memory['work'])
            matches=[t for t in after if bed['def_name'].lower() in t.get('def_name','').lower() and 'blueprint' in t.get('def_name','').lower()]
            if len(matches)!=1 or rt.counters['actions']!=1 or not rt.memory['work'] or rt.memory['work'][-1]['status']!='complete':raise RuntimeError('Blueprint/readback/tracker verification failed.')
            report['passed']=True
        await asyncio.wait_for(cycle(),timeout)
    except BaseException as e:
        report['error']=f'{type(e).__name__}: {e}';raise
    finally:
        report.update(elapsed_seconds=round(time.monotonic()-started,2),counters=rt.counters)
        (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k not in ('definitions','proposals')},indent=2),flush=True)
        print('Evidence: '+str(folder/'report.json'),flush=True)
        await rt.stop();rt.store.close()


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--execute',action='store_true',required=True);p.add_argument('--timeout',type=int,default=240)
    asyncio.run(run(p.parse_args().timeout))
