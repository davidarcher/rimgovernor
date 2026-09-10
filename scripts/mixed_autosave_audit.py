"""Require two native seeds and immutable controller work across real autosaves."""
import argparse
import hashlib
import json
from pathlib import Path


def audit(report):
    assert report['outcome']=='passed',report.get('error')
    assert report['model_calls']==0 and report['save_edits']==[]
    before=report['mixed_before']
    mixed=report['mixed']
    assert len(before['progress'][mixed['shell']]['issued'])==1
    assert len(before['costs'][mixed['shell']])>1
    for name in ('zone','work'):
        pending=before['progress'][mixed[name]]
        assert pending['state']=='pending' and not pending['issued']
    assert report['mixed_after_load']==before
    boundaries=report['mixed_boundaries']
    assert boundaries
    deadline=report['window']['tickDeadline']
    for boundary in boundaries:
        assert boundary['controller']==before,'Controller orders changed during autosave'
        assert boundary['clock']['tickDeadline']==deadline,'Execution deadline changed'
    longs=[b for b in boundaries if b['event']['kind']=='long_event']
    clears=[b for b in boundaries if b['event']['kind']=='force_pause_cleared'
        and b['event'].get('event',{}).get('forcePauseKind')=='long_event']
    assert longs and clears
    for waiting in longs:
        assert any(c['event']['epoch']==waiting['event']['epoch']
            and c['event']['cursor']>waiting['event']['cursor']
            and c['event']['event']['speedRestored'] for c in clears)
    assert report['end']['lastTick']==deadline and report['end']['pauseVerified']
    assert report['end']['stopReason']=='tick_budget'
    assert all(save['tick']<deadline and any(abs(save['tick']-b['event']['tick'])<=1 for b in longs)
        for save in report['autosaves'])
    assert report['autosaves'] and report['stale_write_refusal']
    assert report['identity']['colonyId']==report['loaded_identity']['colonyId']
    assert report['identity']['loadToken']!=report['loaded_identity']['loadToken']
    cleanup=report['cleanup']['structuredContent']
    assert cleanup['code']=='terminated' and cleanup['claimRemoved']
    return {'colony':report['identity']['colonyId'],'deadline':deadline,
        'autosaves':report['autosaves'],'native_actions':before['actions'],
        'pending_steps':[mixed['zone'],mixed['work']],'partial_step':mixed['shell']}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reports',type=Path,nargs=2,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    rows=[]
    for path in args.reports:
        row=audit(json.loads(path.read_text()))
        row.update(report=str(path.resolve()),sha256=hashlib.sha256(path.read_bytes()).hexdigest())
        rows.append(row)
    assert len({row['colony'] for row in rows})==2,'Two different native colonies are required'
    args.output.write_text(json.dumps({'passed':True,'scope':'Mixed native autosave and reload on two seeds','cases':rows},indent=2))
    print('PASS: two native seeds preserve mixed controller work across autosave and reload')
