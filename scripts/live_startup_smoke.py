"""Exercise the dashboard's normal automation with a plain sleeping-arrangements goal.

Requires a fresh disposable quicktest. Supplies no sites, definitions, tool
contracts or model responses. Stops and pauses on success, error or timeout.
Evidence includes actual orders, model-call timings, and observed furniture.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path
import httpx


async def run(timeout, resume=False):
    folder=Path('.rimbot/startup-tests')/time.strftime('%Y%m%d-%H%M%S')
    folder.mkdir(parents=True)
    report={'passed':False,'objective':'Provide sleeping arrangements for everyone.'}
    started=time.time();last_status=None;history=[]
    async with httpx.AsyncClient(base_url='http://127.0.0.1:8787',headers={'X-RimBot':'1'},timeout=30,trust_env=False) as client:
        async def get(path):
            r=await client.get(path);r.raise_for_status();return r.json()
        async def post(path,data=None):
            r=await client.post(path,json=data or {});r.raise_for_status();return r.json()
        state=await get('/api/state')
        if not state['connected'] or state['busy'] or state['mode']!='manual':
            raise RuntimeError('Connect an idle, fresh colony in Manual first.')
        if not resume and (state['memory']['goals'] or state['memory']['work'] or state['memory']['plans']):
            raise RuntimeError('This fixture requires fresh controller state.')
        identity=state['colony'];mid=state['observation']['map']['id']
        report.update(colony=identity,session_id=state['observation']['game']['session_id'])
        try:
            report['resumed']=resume
            if not resume:await post('/api/goals',{'text':report['objective']})
            await post('/rimapi/api/v1/game/speed?speed=3')
            await post('/api/control',{'mode':'automate'})
            while time.time()-started<timeout:
                state=await get('/api/state')
                if state['colony']!=identity:raise RuntimeError('Game changed during the test.')
                status=state['status']
                breadcrumb=(status.get('role','').split(':',1)[0],status.get('phase'))
                if breadcrumb!=last_status:
                    print(f'{time.time()-started:.1f}s: '+': '.join(breadcrumb),flush=True)
                    last_status=breadcrumb
                response=await client.get('/api/history');response.raise_for_status()
                history=[json.loads(line) for line in response.text.splitlines() if line]
                actions=[e for e in history if e['kind']=='action' and e.get('endpoint') and e['at']>=started]
                if actions and 'first_order_seconds' not in report:
                    report['first_order_seconds']=round(actions[0]['at']-started,2)
                    print(f"First order after {report['first_order_seconds']}s: {actions[0]['text']}",flush=True)
                # Exact-cell readback verifies completed standard single beds/spots;
                # this conservative fixture does not assume slots in modded beds.
                buildings=(await get(f'/rimapi/api/v1/map/buildings?map_id={mid}')).get('data',[])
                finished=[]
                for building in buildings:
                    if building.get('def') not in ('Bed','SleepingSpot'):continue
                    raw=await client.request('GET','/rimapi/api/v1/map/things-at',json={'map_id':mid,'position':building['position']})
                    raw.raise_for_status()
                    if any(t.get('thing_id')==building['id'] and t.get('def_name')==building['def'] for t in raw.json().get('data',[])):
                        finished.append(building)
                report['finished']=finished
                if len(finished)>=state['observation']['game']['colonist_count']:
                    report['passed']=True;break
                if not state['busy'] and status.get('phase')=='Needs attention':
                    raise RuntimeError(status.get('detail'))
                await asyncio.sleep(3)
            if not report['passed']:raise TimeoutError('Sleeping arrangements were not verified before the deadline.')
        except BaseException as e:
            report['error']=f'{type(e).__name__}: {e}'
            raise
        finally:
            await post('/api/control',{'mode':'manual'})
            current=await get('/api/state')
            if current['colony']==identity:await post('/rimapi/api/v1/game/speed?speed=0')
            response=await client.get('/api/history')
            (folder/'history.jsonl').write_text(response.text,encoding='utf-8')
            metrics={}
            for line in response.text.splitlines():
                event=json.loads(line)
                if event['kind']!='model_call' or event['at']<started:continue
                role=metrics.setdefault(event['role'],{'calls':0,'seconds':0,'input_tokens':0,'output_tokens':0})
                role['calls']+=1
                role['seconds']=round(role['seconds']+event['seconds'],3)
                role['input_tokens']+=event['usage'].get('prompt_tokens',0)
                role['output_tokens']+=event['usage'].get('completion_tokens',0)
            report['model_metrics']=metrics
            report.update(elapsed_seconds=round(time.time()-started,2),state=current)
            (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
            print(f"Passed: {report['passed']}. Evidence: {folder/'report.json'}",flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--execute',action='store_true',required=True)
    p.add_argument('--timeout',type=int,default=360)
    p.add_argument('--resume',action='store_true',help='Continue the loaded test colony after a fix; report this as a resumed run.')
    args=p.parse_args()
    asyncio.run(run(args.timeout,args.resume))
