"""Verify indexed history on an isolated copy of an existing controller database."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import time
from rimbot.store import Store


def digest(db):
    result=hashlib.sha256()
    for row in db.execute('SELECT id,at,colony,kind,data FROM events ORDER BY id'):
        result.update(json.dumps(row,ensure_ascii=False).encode())
    return result.hexdigest()


def measure(db,colony,diagnostics):
    filtered='' if diagnostics else " AND kind NOT IN ('model_diagnostic','model_call','tool_result','planner_tool')"
    query='SELECT id,at,kind,data FROM events WHERE colony=?'+filtered+' ORDER BY id DESC LIMIT ?'
    start=time.perf_counter()
    rows=db.execute(query,(colony,100)).fetchall()
    return {'rows':rows,'milliseconds':round((time.perf_counter()-start)*1000,3),
        'query_plan':[r[3] for r in db.execute('EXPLAIN QUERY PLAN '+query,(colony,100))]}


def run(args):
    args.output.mkdir(parents=True,exist_ok=False)
    destination=args.output/'audit.sqlite'
    source=sqlite3.connect(args.source.resolve().as_uri()+'?mode=ro',uri=True)
    copy=sqlite3.connect(destination)
    try:source.backup(copy)
    finally:source.close()
    if args.unindexed_baseline:
        copy.execute('DROP INDEX IF EXISTS events_colony_id')
        copy.execute('DROP INDEX IF EXISTS events_visible_colony_id')
    before_digest=digest(copy)
    colonies=[r[0] for r in copy.execute('SELECT DISTINCT colony FROM events WHERE colony IS NOT NULL')]
    absent='audit-absent-colony'
    while absent in colonies:absent+='-unused'
    colonies.append(absent)
    before={(colony,diagnostics):measure(copy,colony,diagnostics) for colony in colonies for diagnostics in (False,True)}
    counts=copy.execute('SELECT COUNT(*),COALESCE(SUM(length(data)),0) FROM events').fetchone()
    goals=[]
    for key,value in copy.execute('SELECT key,value FROM state'):
        plan=json.loads(value).get('current_plan',{})
        for identity,goal in plan.get('colony_goals',{}).items():
            methods=goal.get('evidence',{}).get('methods',{})
            goals.append({'state_key':key,'goal':identity,'method_entries':len(methods),
                'method_bytes':len(json.dumps(methods).encode()),'step_references':len(goal.get('steps',[]))})
    copy.close()
    store=Store(destination)
    report={'scope':'Read-only source, indexed copy; retained-history integrity and query access, not sustained survival',
        'source':str(args.source.resolve()),'events':counts[0],'event_payload_bytes':counts[1],
        'baseline':'unindexed copy' if args.unindexed_baseline else 'source schema',
        'event_digest':before_digest,'goals':goals,'queries':[]}
    try:
        assert digest(store.db)==before_digest,'Index migration changed event history'
        for (colony,diagnostics),prior in before.items():
            after=measure(store.db,colony,diagnostics)
            assert prior['rows']==after['rows']
            expected=[dict(id=r[0],at=r[1],kind=r[2],**json.loads(r[3])) for r in reversed(prior['rows'])]
            assert store.history(colony,100,include_diagnostics=diagnostics)==expected
            assert all('SCAN events' not in row for row in after['query_plan'])
            report['queries'].append({'colony':colony,'diagnostics':diagnostics,'returned':len(expected),
                'before':{k:v for k,v in prior.items() if k!='rows'},
                'after':{k:v for k,v in after.items() if k!='rows'}})
        report['outcome']='passed'
    finally:
        store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--unindexed-baseline',action='store_true',help='Remove history indexes only on the isolated copy before measuring the baseline')
    run(parser.parse_args())
