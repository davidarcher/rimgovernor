"""Migrate a read-only native campaign copy and verify method deduplication after backup."""
import argparse
import json
from pathlib import Path
import sqlite3
from history_query_audit import digest
from rimgovernor.colony_plan import ColonyPlan,Decision,PlanSpec
from rimgovernor.plan_archive import bind_archive,prepare_archive,finish_archive
from rimgovernor.store import Store


def run(args):
    args.output.mkdir(parents=True,exist_ok=False)
    source=sqlite3.connect(args.source.resolve().as_uri()+'?mode=ro',uri=True)
    destination=args.output/'migrated.sqlite'
    with sqlite3.connect(destination) as copy:source.backup(copy)
    source.close()
    store=Store(destination)
    report={'outcome':'failed','scope':'Existing native campaign data, explicit completed-action retirement and SQLite backup; no new native play',
        'source':str(args.source.resolve()),'states':[]}
    expected={}
    events=digest(store.db)
    try:
        for key,value in store.db.execute('SELECT key,value FROM state').fetchall():
            state=json.loads(value)
            if not key.startswith('bridge:') or 'current_plan' not in state:continue
            colony=key.removeprefix('bridge:')
            plan=ColonyPlan.model_validate(state['current_plan']);bind_archive(plan,store,colony)
            expected[key]={identity:dict(goal.evidence.get('methods',{})) for identity,goal in plan.colony_goals.items()}
            before=len(plan.model_dump_json().encode())
            # Exercise normal plan retirement on real completed native outcomes.
            # No synthetic completion, game action or native identity is added.
            retired={s.id for s in plan.spec.steps if s.source=='AUTOPILOT'
                and s.action.kind=='native_operation' and plan.progress[s.id].state=='complete'}
            spec=plan.spec.model_dump()
            spec['steps']=[dict(s.model_dump(),after=[d.model_dump() for d in s.after if d.step not in retired])
                for s in plan.spec.steps if s.id not in retired]
            plan.commit(Decision(expected_revision=plan.revision,disposition='revise',
                assessment='Archive audit',rationale='Retire observed completed actions',reply='Archive audit',
                plan=PlanSpec.model_validate(spec)),actor='strategist',tick=plan.chosen_tick)
            snapshot,records,methods,evidence=prepare_archive(plan)
            state['current_plan']=snapshot
            store.archive_and_set(colony,key,state,records,methods,evidence)
            finish_archive(plan,snapshot,records,methods,evidence)
            assert store.get(key)['current_plan']==plan.model_dump()
            report['states'].append({'key':key,'before_bytes':before,'after_bytes':len(plan.model_dump_json().encode()),
                'retired_completed_actions':len(retired),
                'new_archived_methods':len(methods),'new_archived_actions':len(records)})
        assert expected,'No native controller state found'
        assert any(row['new_archived_methods'] for row in report['states']),'Fixture has no archivable native method evidence'
        assert digest(store.db)==events
        with sqlite3.connect(args.output/'resumed.sqlite') as backup:store.db.backup(backup)
    except Exception as error:
        report['error']=str(error)
        raise
    finally:
        store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    resumed=Store(args.output/'resumed.sqlite')
    try:
        verified=0;reopened=[]
        for key,goals in expected.items():
            colony=key.removeprefix('bridge:')
            plan=ColonyPlan.model_validate(resumed.get(key)['current_plan']);bind_archive(plan,resumed,colony)
            for identity,methods in goals.items():
                goal=plan.colony_goals[identity]
                for name,steps in methods.items():
                    assert goal.method_seen(name)
                    retained=goal.evidence.get('methods',{}).get(name)
                    if retained is None:retained=resumed.retired_method(colony,identity,goal.method_epoch,name)['steps']
                    assert retained==steps
                    verified+=1
                if goal.archived_methods:
                    epoch=goal.method_epoch
                    archived={name:resumed.retired_method(colony,identity,epoch,name) for name in methods
                        if name not in goal.evidence.get('methods',{})}
                    goal.reopen_methods()
                    assert goal.method_epoch==epoch+1 and not goal.archived_methods
                    assert all(not goal.method_seen(name) for name in methods)
                    assert all(resumed.retired_method(colony,identity,epoch,name)==record for name,record in archived.items())
                    reopened.append(identity)
        assert digest(resumed.db)==events
        report.update(outcome='passed',verified_methods=verified,reopened_goals=reopened,event_digest=events)
    finally:
        resumed.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    run(parser.parse_args())
