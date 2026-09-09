"""Measure durable plan growth using synthetic completed actions and native receipt shapes.

No native game calls or inference. This isolates retention overhead; it does not
certify recovery or the legality of synthetic pawn identifiers.
"""
import argparse
import json
from pathlib import Path
import time
from rimbot.colony_plan import ColonyPlan, ColonyGoal, CommitSteps, PlanStep, StepProgress
from rimbot.store import Store
from rimbot.plan_archive import bind_archive,prepare_archive,finish_archive


def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    source=json.loads(args.evidence.read_text())['plan']
    template=next(p['issued'] for p in source['progress'].values()
                  if p['state']=='complete' and p['issued'])
    store=Store(args.output/'audit.sqlite')
    plan=ColonyPlan()
    if args.lifecycle:
        plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2)
    bind_archive(plan,store,'audit')
    report={'scope':'Synthetic retention audit using observed receipt shape; no native execution',
            'source_evidence':str(args.evidence.resolve()),'samples':[]}
    try:
        for index in range(args.steps):
            identity=f'audit-{index}'
            step=PlanStep(id=identity,title='Work coverage',source='AUTOPILOT',
                goal_id='EnsureWorkAssignments' if args.lifecycle else None,
                completion_criteria='Synthetic completed outcome; no game write',
                action={'kind':'native_operation','tool':'home/pawn_config',
                        'arguments':{'pawn':f'Thing_Human{index}','work':'Growing=1','dryRun':False}})
            plan.commit(CommitSteps(expected_revision=plan.revision,reason='Retention workload',steps=[step]).decision(plan),
                        actor='strategist',tick=index*600)
            plan.progress[identity]=StepProgress(state='complete',issued=template)
            if args.lifecycle:
                goal=plan.colony_goals['EnsureWorkAssignments']
                method=f'coverage-{index}'
                goal.steps.append(identity)
                goal.evidence.setdefault('methods',{})[method]=[identity]
                store.event('audit','htn_method_selected',goal_id='EnsureWorkAssignments',method=method)
                for _ in range(10):
                    store.event('other-colony','tool_result',result=template)
                    store.event('audit','tool_result',result=template)
            snapshot,records,methods,evidence=prepare_archive(plan)
            store.archive_and_set('audit','plan',snapshot,records,methods,evidence)
            finish_archive(plan,snapshot,records,methods,evidence)
            if (index+1)%100==0 or index+1==args.steps:
                started=time.perf_counter()
                encoded=plan.model_dump_json()
                serialize_ms=(time.perf_counter()-started)*1000
                reloaded=ColonyPlan.model_validate(store.get('plan'))
                bind_archive(reloaded,store,'audit')
                assert reloaded.model_dump()==plan.model_dump()
                assert all(p.issued==template for p in reloaded.progress.values())
                store.db.execute('PRAGMA wal_checkpoint(TRUNCATE)')
                row={'completed_actions':index+1,'active_steps':len(plan.spec.steps),
                     'retired_steps':len(plan.control.get('retired_steps',{})),
                     'archived_steps':plan.control.get('archived_action_count',0),
                     'progress_records':len(plan.progress),'history_entries':len(plan.history),
                     'snapshot_bytes':len(encoded.encode()),'sqlite_bytes':(args.output/'audit.sqlite').stat().st_size,
                     'serialization_ms':round(serialize_ms,3)}
                if args.lifecycle:
                    goal=plan.colony_goals['EnsureWorkAssignments']
                    row['method_entries']=len(goal.evidence['methods'])
                    row['archived_methods']=goal.archived_methods
                    assert goal.archived_methods+len(goal.evidence['methods'])==index+1
                    row['method_bytes']=len(json.dumps(goal.evidence['methods']).encode())
                    row['events']=store.db.execute('SELECT COUNT(*) FROM events').fetchone()[0]
                    row['event_payload_bytes']=store.db.execute('SELECT COALESCE(SUM(length(data)),0) FROM events').fetchone()[0]
                    started=time.perf_counter()
                    visible=store.history('audit',100)
                    row['recent_history_ms']=round((time.perf_counter()-started)*1000,3)
                    assert len(visible)==100 and all(e['kind']=='htn_method_selected' for e in visible)
                    assert visible[-1]['method']==method
                    for diagnostics in (False,True):
                        filtered='' if diagnostics else " AND kind NOT IN ('model_diagnostic','model_call','tool_result','planner_tool')"
                        query='SELECT id,at,kind,data FROM events WHERE colony=?'+filtered+' ORDER BY id DESC LIMIT ?'
                        description=[r[3] for r in store.db.execute('EXPLAIN QUERY PLAN '+query,('audit',100))]
                        row['query_plan_'+str(diagnostics)]=description
                        assert all('SCAN events' not in detail for detail in description),description
                report['samples'].append(row)
                (args.output/'result.json').write_text(json.dumps(report,indent=2))
                print(json.dumps(row),flush=True)
                plan=reloaded
        for index in range(args.steps):
            identity=f'audit-{index}'
            receipt=plan.progress[identity].issued if identity in plan.progress else store.retired_action('audit',identity)['progress']['issued']
            assert receipt==template
            if args.lifecycle:
                goal=plan.colony_goals['EnsureWorkAssignments']
                name=f'coverage-{index}'
                assert goal.method_seen(name)
                steps=goal.evidence['methods'].get(name)
                if steps is None:steps=store.retired_method('audit','EnsureWorkAssignments',goal.method_epoch,name)['steps']
                assert steps==[identity]
    finally:
        store.close()
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence',type=Path,required=True,help='Native trial result.json containing plan/progress')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--steps',type=int,default=1000)
    parser.add_argument('--lifecycle',action='store_true',help='Include persistent method evidence and interleaved colony/diagnostic events')
    args=parser.parse_args()
    if not 100<=args.steps<=10000: parser.error('--steps must be between 100 and 10000')
    run(args)
