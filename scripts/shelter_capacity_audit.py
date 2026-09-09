"""Audit larger starter shelter and fragmented-field outcomes in a native report."""
import argparse
import hashlib
import json
from pathlib import Path


def audit(source):
    report=json.loads(source.read_text())
    count=len(report['starting_colonists'])
    assert count>8 and report['model_calls']==report['model_attempts']==0
    plan=report['plan'];facts=plan['control']['facts']
    assert facts['colonists']==count
    assert facts['indoorSleepingCapacity']>=count and facts['bedCapacity']>=count
    assert plan['control']['criteria']['shelter'] and plan['control']['criteria']['production']
    shell=[s for s in plan['spec']['steps'] if s['goal_id']=='EnsureInitialShelter' and s['action']['kind']=='build_room_shell']
    assert len(shell)==1 and plan['progress'][shell[0]['id']]['state']=='complete'
    sleeping=[s for s in plan['spec']['steps'] if s['goal_id']=='EnsureInitialShelter'
        and s['action']['kind']=='place_buildings' and any(p['def_name']=='SleepingSpot' for p in s['action']['placements'])]
    assert sleeping and all(plan['progress'][s['id']]['state']=='complete' for s in sleeping)
    fields=[s for s in plan['spec']['steps'] if s['goal_id']=='EnsureFoodSupply'
        and s['action']['kind']=='create_zone' and s['action']['zone_type']=='growing']
    assert fields and all(plan['progress'][s['id']]['state']=='complete' for s in fields)
    patches=[p for s in fields for p in s['action']['patches']]
    assert any(p['width']<4 or p['height']<4 for p in patches)
    assert any(f.get('edible') and f.get('growingCells',0)>=count*10 for f in facts['farms'])
    return {'outcome':'passed','scope':'Native indoor sleeping capacity and growing cells; no sustained survival or bed-use claim',
        'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'tick':facts['tick'],
        'colonists':count,'indoor_sleeping_capacity':facts['indoorSleepingCapacity'],
        'farm_observations':facts['farms'],'shell_step':shell[0]['id'],
        'sleeping_steps':[s['id'] for s in sleeping],'patches':patches,
        'campaign_outcome':report['outcome']}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();result=audit(args.report)
    args.output.write_text(json.dumps(result,indent=2))
    print(json.dumps({'outcome':result['outcome'],'colonists':result['colonists'],
        'indoor_sleeping_capacity':result['indoor_sleeping_capacity']}))
