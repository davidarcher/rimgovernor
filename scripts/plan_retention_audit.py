"""Measure durable plan growth using synthetic completed actions and native receipt shapes.

No native game calls or inference. This isolates retention overhead; it does not
certify recovery or the legality of synthetic pawn identifiers.
"""
import argparse
import json
from pathlib import Path
import time
from rimbot.colony_plan import ColonyPlan, CommitSteps, PlanStep, StepProgress
from rimbot.store import Store
from rimbot.plan_archive import bind_archive,prepare_archive,finish_archive


def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    source=json.loads(args.evidence.read_text())['plan']
    template=next(p['issued'] for p in source['progress'].values()
                  if p['state']=='complete' and p['issued'])
    store=Store(args.output/'audit.sqlite')
    plan=ColonyPlan()
    bind_archive(plan,store,'audit')
    report={'scope':'Synthetic retention audit using observed receipt shape; no native execution',
            'source_evidence':str(args.evidence.resolve()),'samples':[]}
    try:
        for index in range(args.steps):
            identity=f'audit-{index}'
            step=PlanStep(id=identity,title='Work coverage',source='AUTOPILOT',
                completion_criteria='Synthetic completed outcome; no game write',
                action={'kind':'native_operation','tool':'home/pawn_config',
                        'arguments':{'pawn':f'Thing_Human{index}','work':'Growing=1','dryRun':False}})
            plan.commit(CommitSteps(expected_revision=plan.revision,reason='Retention workload',steps=[step]).decision(plan),
                        actor='strategist',tick=index*600)
            plan.progress[identity]=StepProgress(state='complete',issued=template)
            snapshot,records=prepare_archive(plan)
            store.archive_and_set('audit','plan',snapshot,records)
            finish_archive(plan,snapshot,records)
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
                report['samples'].append(row)
                (args.output/'result.json').write_text(json.dumps(report,indent=2))
                print(json.dumps(row),flush=True)
                plan=reloaded
        for index in range(args.steps):
            identity=f'audit-{index}'
            receipt=plan.progress[identity].issued if identity in plan.progress else store.retired_action('audit',identity)['progress']['issued']
            assert receipt==template
    finally:
        store.close()
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence',type=Path,required=True,help='Native trial result.json containing plan/progress')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--steps',type=int,default=1000)
    args=parser.parse_args()
    if not 100<=args.steps<=10000: parser.error('--steps must be between 100 and 10000')
    run(args)
