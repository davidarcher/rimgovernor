"""Verify multi-batch work settings against a native foothold report's readbacks."""
import argparse
import hashlib
import json
from pathlib import Path


def audit(source):
    report=json.loads(source.read_text())
    assert report['model_calls']==report['model_attempts']==0
    assert len(report['starting_colonists'])>8
    plan=report['plan']
    steps=[s for s in plan['spec']['steps'] if s.get('goal_id')=='EnsureWorkAssignments'
        and plan['progress'][s['id']]['state']=='complete']
    batches={};verified=[]
    events=[e.get('data',e) for e in report['events']]
    for step in steps:
        action=step['action'];args=action['arguments']
        assert action['tool']=='home/pawn_config' and args['dryRun'] is False
        matches=[e for e in events if e.get('text')=='home/pawn_config' and e.get('arguments')==args]
        assert matches,step['id']
        def conforms(event):
            work=event.get('result',{}).get('after',{}).get('work',{})
            if not work.get('hasWorkSettings'):return False
            types={t['name']:t for t in work.get('types',[])}
            for item in args['work'].split(','):
                name,value=item.split('=');value=int(value)
                row=types.get(name,{})
                if work.get('manualPriorities'):
                    if row.get('priorityStored')!=value:return False
                elif row.get('priority') is None or (row['priority']>0)!=(value>0):return False
            return True
        assert any(conforms(e) for e in matches),step['id']
        batch=step['id'].rsplit('-',1)[0]
        batches.setdefault(batch,[]).append(args['pawn'])
        verified.append({'step':step['id'],'pawn':args['pawn'],'work':args['work']})
    assert len({r['pawn'] for r in verified})>8
    assert len(batches)>1 and any(len(pawns)==8 for pawns in batches.values())
    assert any(r.get('criteria',{}).get('work') is True for r in report['history'])
    return {'outcome':'passed','scope':'Native work-setting batches and readback; no pawn-labor or new-arrival claim',
        'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
        'starting_pawns':len(report['starting_colonists']),'verified':verified,'batches':batches,
        'campaign_outcome':report['outcome']}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=audit(args.report)
    args.output.write_text(json.dumps(result,indent=2))
    print(json.dumps({'outcome':result['outcome'],'pawns':len({r['pawn'] for r in result['verified']})}))
