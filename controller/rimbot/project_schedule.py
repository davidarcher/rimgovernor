"""Game-time commitments for the existing project executor, not another task queue."""
import hashlib
import json

# A blocked attempt gets a short reassessment; underway work gets six game hours.
RETRY_TICKS = 2500
REVIEW_TICKS = 15000


def evidence(project, memory):
    progress = {k: v for k, v in project.get('progress', {}).items() if k != 'observed_tick'}
    if 'sites' in progress:
        # Ordinary work increments are success, not a reason to ask for more orders.
        progress['sites'] = [{k:v for k,v in site.items() if k not in ('work_done','work_total','pawn_ids')}
                             for site in progress['sites']]
    if project.get('kind') == 'growing' and project.get('crop_def'):
        # Only this crop's zone geometry/settings require execution decisions.
        # Normal growth and individual sow/harvest jobs must not summon the LLM.
        # The bounded reassessment still checks missing labor and crop losses.
        progress = {k:v for k,v in progress.items() if k not in
                    ('zones','zone_count','designated_cells')}
        zones = project.get('progress', {}).get('zones')
        if zones is not None:
            progress['zones'] = sorted(
                [{k:z.get(k) for k in ('zone_id','cells','crop','sowing_allowed')}
                 for z in zones if z.get('crop') == project['crop_def']],
                key=lambda z:str(z['zone_id']))
    ids = set(project.get('work_ids', []))
    orders = [(w['id'], w['status']) for w in memory.get('work', []) if w['id'] in ids]
    intent = {k: project.get(k) for k in ('kind', 'outcome', 'quantity', 'crop_def',
              'target_cells', 'definition_requirements', 'constraints', 'success_signals','after_projects','deadline_tick','work_policy')}
    return hashlib.sha256(json.dumps([intent, progress, sorted(orders)], sort_keys=True).encode()).hexdigest()


def execution_due(project, memory, tick, force=False):
    """Changed observations reopen work immediately; elapsed wall time never does."""
    if project.get('status') in ('retired','suspended'):
        return False, 'Project '+project['status']
    if force:
        return True, 'Player direction or urgent review'
    previous = project.get('execution_review')
    if previous is None:
        return True, 'First execution review'
    if evidence(project, memory) != previous['evidence']:
        return True, 'Observed work or objective changed'
    interval = RETRY_TICKS if project.get('status') == 'needs_review' else REVIEW_TICKS
    if tick - previous['tick'] >= interval:
        return True, 'Scheduled reassessment'
    return False, 'Waiting for observed change or game-time reassessment'


def record_attempt(project, memory, tick):
    project['execution_review'] = {'tick': tick, 'evidence': evidence(project, memory)}
    project.pop('execution_skip_reason', None)
