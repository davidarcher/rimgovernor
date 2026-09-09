"""Score native lifecycle evidence without relabeling it sustained food acceptance."""
import argparse
import hashlib
import json
from pathlib import Path
from rimbot.colony_policy import work_assignment


def audit(report):
    life=report['lifecycle'];ledger=life['ledger'];beds=life['bed_use']
    assert report['outcome']=='LIFECYCLE_WINDOW',report['outcome']
    assert report['model_calls']==0 and report['model_attempts']==0
    assert ledger and ledger[-1]['tick']-report['initial_game_tick']>=life['days']*60000
    for a,b in zip(ledger,ledger[1:]):
        assert b['tick']>=a['tick']
        assert all(b['table_rows'][name]>=count for name,count in a['table_rows'].items())
    starting=set(report['starting_colonists'])
    joined={pawn for event in report.get('join_incidents',[]) for pawn in event['result']['joined']}
    used={p['id'] for sample in beds for p in sample['pawns'] if p['in_bed'] is True and p['bed']}
    assert starting<=used,'Starting colonists without observed native bed use: '+str(sorted(starting-used))
    assert joined<=used,'Joined colonists without observed native bed use: '+str(sorted(joined-used))
    native_assignments=[]
    for sample in beds:
        people=[dict(p,thingId=p['id']) for p in sample['pawns']]
        if not joined<={p['thingId'] for p in people}:continue
        wanted,covered=work_assignment(people)
        if not covered:continue
        by_id={p['thingId']:p for p in people}
        if all(all(any(w['name']==name and (w.get('priorityStored')==value if by_id[pawn]['work'].get('manualPriorities')
            else (w.get('priority',0)>0)==(value>0)) for w in by_id[pawn]['work']['types'])
            for name,value in assignments.items()) for pawn,assignments in wanted.items()):
            native_assignments.append(sample['tick'])
    assert native_assignments,'No native roster-wide work readback matched the deterministic allocator'
    events=[entry['event'] for entry in life['boundaries']]
    longs=[e for e in events if e['kind']=='long_event']
    clears=[e for e in events if e['kind']=='force_pause_cleared' and e.get('event',{}).get('forcePauseKind')=='long_event']
    assert longs and clears,'No ordinary autosave long-event recovery was measured'
    assert life.get('autosaves'),'No native autosave file was retained'
    assert all(any(abs(e['tick']-save['tick'])<=1 for e in longs) for save in life['autosaves'])
    assert ledger[-1]['tick']>max(e['tick'] for e in longs)
    return {'passed':True,'scope':'Bounded native lifecycle, immutable ledger growth, actual bed use and work allocation; food gates separate',
        'ticks':ledger[-1]['tick']-report['initial_game_tick'],'starting':sorted(starting),'joined':sorted(joined),
        'observed_bed_users':sorted(used),'native_work_match_ticks':native_assignments,
        'initial_ledger':{k:v for k,v in ledger[0].items() if k!='actions'},
        'final_ledger':{k:v for k,v in ledger[-1].items() if k!='actions'},
        'max_live_progress':max(row['live_progress'] for row in ledger),'max_snapshot_bytes':max(row['snapshot_bytes'] for row in ledger),
        'max_recent_history_ms':max(row['recent_history_ms'] for row in ledger),'autosave_long_events':longs,'autosave_clears':clears}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=audit(json.loads(args.report.read_text()))
    result.update(source=str(args.report.resolve()),sha256=hashlib.sha256(args.report.read_bytes()).hexdigest())
    args.output.write_text(json.dumps(result,indent=2))
    print(json.dumps({'passed':True,'ticks':result['ticks'],'joined':result['joined']}))
