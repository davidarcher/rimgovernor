"""Actual local model -> five managers -> administrator -> native order -> readback.

Run with --execute against a disposable loaded colony. Main controller must be
Manual. Changes one available work priority, then restores it. No mock model.
Evidence and failures are saved under .rimbot/live-tests.
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


async def run(timeout=240):
    async with httpx.AsyncClient() as client:
        state=(await client.get('http://127.0.0.1:8787/api/state')).json()
    if state['mode']!='manual' or state['busy']:
        raise RuntimeError('Set the dashboard to Manual before the test.')
    folder=Path('.rimbot/live-tests')/time.strftime('%Y%m%d-%H%M%S')
    folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings(**state['settings']))
    report={'passed':False,'model':rt.settings.model}
    started=time.monotonic()
    original=None;target=None;attempted=False
    try:
        await rt.poll()
        rt.cycle_generation=rt.generation
        rt.mode='automate'
        pawns=await rt.api.call('get_colonists_detailed',{})
        pawn=next(p for p in pawns if any(not w['is_totally_disabled'] for w in p['colonist_work_info']['work_priorities']))
        pawn=await rt.api.call('get_colonist_detailed',{'id':pawn['colonist']['id']})
        work=next(w for w in pawn['colonist_work_info']['work_priorities'] if not w['is_totally_disabled'])
        original={'id':pawn['colonist']['id'],'work':work['work_type'],'priority':work['priority']}
        target={**original,'priority':0 if original['priority'] else 3}
        async def read_priority():
            p=await rt.api.call('get_colonist_detailed',{'id':original['id']})
            return next((w['priority'] for w in p['colonist_work_info']['work_priorities'] if w['work_type']==original['work']),0)
        report.update(before=original,target=target)
        context={'player_direction':f"Integration test objective only: set pawn {target['id']}'s {target['work']} work priority to {target['priority']}. Workforce owns this change. Other managers should submit no actions unless they identify a conflict with this objective. Do not address other colony needs in this test. There must be exactly one native order. Verify the specific pawn and work type, not unrelated state.",
                 'selected_map':rt.observation['map'],'observed_pawn':pawn,
                 'relevant_contracts':[rt.catalog.get(n) for n in ('post_colonist_work_priority','get_colonist_detailed')],
                 'query_result_shape':{'endpoint':'get_colonist_detailed','arguments':{'id':target['id']},'list_path':'colonist_work_info.work_priorities','row_identity':{'work_type':target['work']},'observed_value':original['priority'],'note':'Use the endpoint response semantics in relevant_contracts for verification; disabled work is omitted from the list.'}}
        async def cycle():
            proposals,decision=await rt.coordinate(context,list(ROLES))
            report['proposals']=proposals
            if set(proposals)!=set(ROLES):raise RuntimeError('Not every manager returned a valid proposal.')
            report['decision']=decision.model_dump()
            actions=[(role,a) for role in decision.accepted for a in Proposal.model_validate(proposals[role]).actions]
            if len(actions)!=1:raise RuntimeError(f'Expected one approved order, received {len(actions)}.')
            role,action=actions[0]
            if role!='Workforce' or action.endpoint!='post_colonist_work_priority' or action.arguments!=target:
                raise RuntimeError('Approved action differs from the bounded test objective.')
            if await rt.check(action.done):raise RuntimeError('Model completion check incorrectly says the unchanged state is complete.')
            nonlocal attempted
            attempted=True
            await rt.execute(action,role)
            report['after']=await read_priority()
            report['work']=rt.memory['work']
            if report['after']!=target['priority'] or rt.counters['actions']!=1 or rt.memory['work'][-1]['status']!='complete':
                raise RuntimeError('Native order/readback/completion verification failed.')
            report['passed']=True
        await asyncio.wait_for(cycle(),timeout)
    except BaseException as e:
        report['error']=f'{type(e).__name__}: {e}'
        raise
    finally:
        try:
            if attempted:
                await rt.api.call('post_colonist_work_priority',original,write=True)
                report['restored']=await read_priority()==original['priority']
                if not report['restored']:report['passed']=False
        except BaseException as e:
            report.update(passed=False,restored=False,cleanup_error=str(e))
            raise
        finally:
            report['elapsed_seconds']=round(time.monotonic()-started,2)
            report['counters']=rt.counters
            (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
            print(json.dumps(report,indent=2),flush=True)
            print('Evidence: '+str(folder/'report.json'),flush=True)
            await rt.stop();rt.store.close()
    if not report['passed']:raise RuntimeError('Test failed; see evidence.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute',action='store_true',required=True)
    parser.add_argument('--timeout',type=int,default=240)
    args=parser.parse_args()
    asyncio.run(run(args.timeout))
