"""Read-only native campaign retention and bed-use measurements."""
import json
import time
from copy import deepcopy


def ledger_sample(rt,path):
    plan=rt.current_plan
    started=time.perf_counter();rt.store.history(rt.colony,100,include_diagnostics=True)
    recent_ms=(time.perf_counter()-started)*1000
    tables=('events','retired_actions','retired_methods','retired_goal_evidence')
    return {'tick':rt.batch.summary.end_tick,'snapshot_bytes':len(plan.model_dump_json().encode()),
        'sqlite_bytes':path.stat().st_size,'wal_bytes':path.with_name(path.name+'-wal').stat().st_size if path.with_name(path.name+'-wal').exists() else 0,
        'sqlite_page_bytes':rt.store.db.execute('PRAGMA page_count').fetchone()[0]*rt.store.db.execute('PRAGMA page_size').fetchone()[0],
        'table_rows':{table:rt.store.db.execute('SELECT COUNT(*) FROM '+table).fetchone()[0] for table in tables},
        'recent_history_ms':round(recent_ms,3),'live_progress':len(plan.progress),
        'goal_evidence_bytes':{key:len(json.dumps(goal.evidence).encode()) for key,goal in plan.colony_goals.items()},
        'hunting_targets':sum(len(g.evidence.get('hunting_targets',{})) for g in plan.colony_goals.values()),
        'recovery_entries':sum(len(p.recovery_history) for p in plan.progress.values()),
        'actions':{s.id:{'signature':s.signature(),'state':plan.progress[s.id].state,
            'issued':deepcopy(plan.progress[s.id].issued)} for s in plan.spec.steps}}


async def bed_use_sample(rt):
    roster=await rt.game.query('home/list_pawns',colonistsOnly=True,health=True,work=True,bio=True)
    if roster.get('success') is not True:raise ValueError('Native bed-use roster unavailable')
    return {'tick':rt.batch.summary.end_tick,'pawns':[{'id':p['thingId'],'job':p.get('job'),
        'dead':p.get('dead'),'downed':p.get('downed'),'drafted':p.get('drafted'),'mentalState':p.get('mentalState'),
        'position':p.get('position'),'in_bed':(p.get('health') or {}).get('inBed'),
        'bed':(p.get('health') or {}).get('bedThingId'),'work':p.get('work'),'bio':p.get('bio')} for p in roster['pawns']]}
