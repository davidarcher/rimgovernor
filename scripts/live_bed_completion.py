"""Finish an existing test bed through real managers and ordinary colony work.

Pass the successful live_construction_smoke report with --blueprint-report.
No writes are supplied to the model. The fixture bounds writes to enabling
nearby timber and colony work, then independently watches the finished ThingDef.
Leaves the bed and manager orders in the disposable game; pauses on exit.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path

import httpx
from rimbot.config import Settings
from rimbot.contracts import Proposal
from rimbot.rimapi import compact
from rimbot.runtime import Runtime
from rimbot.store import Store


async def run(source, timeout):
    fixture=json.loads(Path(source).read_text(encoding='utf-8'))
    if not fixture.get('passed'):raise ValueError('Blueprint fixture must have passed.')
    async with httpx.AsyncClient() as client:
        state=(await client.get('http://127.0.0.1:8787/api/state')).json()
    if state['mode']!='manual' or state['busy']:raise RuntimeError('Dashboard must be idle in Manual.')
    folder=Path('.rimbot/bed-completion-tests')/time.strftime('%Y%m%d-%H%M%S')
    folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings(**state['settings']))
    report={'passed':False,'fixture':str(source),'rounds':[],'transitions':[]}
    started=time.monotonic()
    site=fixture['site'];mid=fixture['map_id'];bed,wood=fixture['definitions']
    async def at_site():
        return await rt.api.call('get_map_things_at',{'map_id':mid,'position':site})
    async def observe():
        things=await at_site()
        names=[t.get('def_name') for t in things]
        if not report['transitions'] or report['transitions'][-1]['defs']!=names:
            report['transitions'].append({'seconds':round(time.monotonic()-started,1),'defs':names,'things':things})
            print('Construction: '+', '.join(names),flush=True)
        return any(t.get('def_name')==bed['def_name'] for t in things)
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        session=rt.observation['game']['session_id']
        if rt.observation['map']['id']!=mid:raise RuntimeError('Wrong map.')
        things=await at_site()
        if not any(t.get('thing_id') in {x['thing_id'] for x in fixture['after']} for t in things):
            raise RuntimeError('Original blueprint no longer exists at the fixture site.')
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':3})
        deadline=started+timeout
        for iteration in range(3):
            if await observe():report['passed']=True;break
            await rt.poll()
            if rt.observation['game']['session_id']!=session:raise RuntimeError('Game changed during test.')
            rt.cycle_generation=rt.generation
            # Test bounds: ordinary nearby timber, never remote cave supplies.
            loose=await rt.api.call('get_map_things',{'map_id':mid})
            timber=[t for t in loose if t.get('def_name')==wood['def_name'] and
                    sum((t.get('position',{}).get(k,10000)-site[k])**2 for k in ('x','z'))<=40**2]
            ids={t['thing_id'] for t in timber}
            context={'player_direction':'Finish the one existing bed at the supplied site. Do not place any additional construction. Diagnose and enable the ordinary work needed, then let colonists build it. Report blockers precisely.',
                     'colony':compact(rt.observation,18000),'bed_site':site,'things_at_site':await at_site(),
                     'nearby_timber':timber,'previous_rounds':compact(report['rounds'],6000),
                     'capabilities':rt.catalog.listing()}
            print(f'Manager review {iteration+1}',flush=True)
            proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Infrastructure','Survival','Workforce']),max(1,deadline-time.monotonic()))
            report['rounds'].append({'proposals':proposals,'decision':decision.model_dump()})
            print(decision.response,flush=True)
            for role in decision.accepted:
                for action in Proposal.model_validate(proposals[role]).actions:
                    if action.endpoint=='post_things_set_forbidden':
                        if action.arguments.get('forbidden') is not False or not set(action.arguments.get('thing_ids',[]))<=ids:
                            raise RuntimeError('Allow proposal exceeded nearby timber fixture bounds.')
                    elif action.endpoint not in ('post_colonist_work_priority','post_pawn_job','post_colonist_time_assignment'):
                        raise RuntimeError('Unexpected test action: '+action.endpoint)
                    await rt.execute(action,role)
                    print('Order: '+action.title,flush=True)
            # Give ordinary work time before asking the model for another diagnosis.
            until=min(deadline,time.monotonic()+60)
            while time.monotonic()<until:
                if await observe():report['passed']=True;break
                await asyncio.sleep(3)
            if report['passed']:break
            if time.monotonic()>=deadline:raise TimeoutError('Bed did not finish within the test deadline.')
        if not report['passed']:raise RuntimeError('Bed unfinished after three manager reviews.')
    except BaseException as e:
        report['error']=f'{type(e).__name__}: {e}'
        raise
    finally:
        report.update(elapsed_seconds=round(time.monotonic()-started,2),counters=rt.counters,work=rt.memory['work'])
        (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(f"Passed: {report['passed']}. Evidence: {folder/'report.json'}",flush=True)
        try:await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        finally:await rt.stop();rt.store.close()


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--execute',action='store_true',required=True)
    p.add_argument('--blueprint-report',required=True)
    p.add_argument('--timeout',type=int,default=420)
    args=p.parse_args()
    asyncio.run(run(args.blueprint_report,args.timeout))
