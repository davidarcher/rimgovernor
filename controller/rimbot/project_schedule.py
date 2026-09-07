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
    ids = set(project.get('work_ids', []))
    orders = [(w['id'], w['status']) for w in memory.get('work', []) if w['id'] in ids]
    intent = {k: project.get(k) for k in ('kind', 'outcome', 'quantity', 'crop_def',
              'target_cells', 'definition_requirements', 'constraints', 'success_signals','after_projects','deadline_tick')}
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
