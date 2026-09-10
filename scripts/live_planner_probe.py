"""Bounded real-model probe against a loaded fresh colony; leaves it in Manual."""
import argparse
import json
import time
from pathlib import Path
import httpx


def run(url, seconds, output):
    report = {'kind':'live_model_probe', 'starter_base_verified':False, 'events':[],
              'first_order_seconds':None, 'first_completed_step_seconds':None}
    with httpx.Client(base_url=url, timeout=20, trust_env=False, headers={'X-RimGovernor':'1'}) as client:
        def get(path):
            response=client.get(path); response.raise_for_status(); return response.json()
        def control(mode):
            response=client.post('/api/control',json={'mode':mode}); response.raise_for_status()
        initial=get('/api/state'); health=get('/api/health')
        if not initial['connected'] or initial['mode'] != 'manual' or initial['currentPlan']['steps']:
            raise RuntimeError('Load a fresh colony with an empty plan and select Manual before probing.')
        identity=(health['pid'],initial['sessionId'])
        events=get('/api/diagnostics')['events']
        cursor=max((e['id'] for e in events),default=0)
        baseline=initial['counters'].copy()
        report.update(session_id=identity[1],controller_pid=identity[0],initial=initial)
        start=time.monotonic(); last_print=-15; started=False
        try:
            started=True
            control('automate')
            while time.monotonic()-start < seconds:
                state=get('/api/state')
                if (get('/api/health')['pid'],state['sessionId']) != identity:
                    raise RuntimeError('Controller or colony changed during probe')
                elapsed=round(time.monotonic()-start,2)
                report['final']=state
                fresh=[e for e in get('/api/diagnostics')['events'] if e['id']>cursor]
                if fresh:
                    report['events'].extend(fresh); cursor=max(e['id'] for e in fresh)
                actions=state['counters']['actions']-baseline['actions']
                complete=sum(s['state']=='complete' for s in state['currentPlan']['steps'])
                if actions>0 and report['first_order_seconds'] is None:
                    report['first_order_seconds']=elapsed
                if complete and report['first_completed_step_seconds'] is None:
                    report['first_completed_step_seconds']=elapsed
                if elapsed-last_print>=15:
                    print(json.dumps({'seconds':elapsed,'actions':actions,'completed_steps':complete,
                        'model_calls':state['counters']['model_calls']-baseline['model_calls']}),flush=True)
                    last_print=elapsed
                if not state['connected'] or state['mode'] != 'automate':
                    report['stopped_reason']='Connection lost or automation stopped'; break
                time.sleep(2)
            report['outcome']='orders_observed' if report['first_order_seconds'] is not None else 'no_orders'
        except BaseException as error:
            report['outcome']='interrupted'; report['error']=str(error)
            raise
        finally:
            try:
                if started and (get('/api/health')['pid'],get('/api/state')['sessionId']) == identity:
                    control('manual')
                    report['left_in_manual']=True
            finally:
                report['elapsed_seconds']=round(time.monotonic()-start,2)
                report['rejected_calls']=sum(e.get('kind')=='planner_tool' and e.get('outcome')=='rejected' for e in report['events'])
                output.parent.mkdir(parents=True,exist_ok=True)
                output.write_text(json.dumps(report,indent=2),encoding='utf-8')
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url',default='http://127.0.0.1:8787')
    parser.add_argument('--seconds',type=int,default=180)
    parser.add_argument('--output',type=Path,default=Path('.rimgovernor/live-planner-probe.json'))
    args=parser.parse_args()
    if args.seconds<1: parser.error('--seconds must be positive')
    result=run(args.url,args.seconds,args.output)
    print(json.dumps({k:result[k] for k in ('outcome','first_order_seconds','rejected_calls')}))
