"""Longitudinal native health evidence; orders never certify recovery."""
from .strategic_state import fingerprint


def care_state(people, previous=None):
    previous = previous or {}
    tracked = {p['patient'] for p in previous.get('patients', [])} | set(previous.get('unknown', []))
    current = {p['thingId'] for p in people if p.get('dead') is False}
    patients, unknown = [], sorted(tracked - current)
    for pawn in people:
        if pawn.get('dead') is True:
            continue
        health = pawn.get('health') or {}
        if health.get('careObservationVersion') != 1:
            unknown.append(pawn['thingId'])
            continue
        if (pawn.get('dead') is not False or not isinstance(health.get('shouldSeekMedicalRest'), bool)
                or not isinstance(health.get('needsTend'), bool) or not isinstance(health.get('hediffs'), list)):
            unknown.append(pawn['thingId'])
            continue
        conditions = [{key: h.get(key) for key in ('id', 'defName', 'partIndex', 'severity',
            'permanent', 'immunizable', 'immunity', 'fullyImmune', 'tendableNow', 'nextTendInTicks',
            'tendExpiresInTicks', 'lifeThreatening')} for h in health['hediffs'] if h.get('isBad') is True]
        if conditions or health['shouldSeekMedicalRest']:
            patients.append(dict(patient=pawn['thingId'], needsTend=health['needsTend'],
                needsRest=health['shouldSeekMedicalRest'], inBed=health.get('inBed'),
                bed=health.get('bedThingId'), care=health.get('medicalCare'), conditions=conditions))
    return dict(patients=patients, unknown=sorted(set(unknown)))


def resting_patients(rt):
    """Request bounded native monitoring only for this review's resting patients."""
    goal=rt.current_plan.colony_goals.get('MaintainMedicalCare')
    facts=rt.current_plan.control.get('facts',{})
    if (not goal or goal.cancelled or goal.status not in ('active','complete') or not rt.batch
            or facts.get('tick')!=rt.batch.summary.end_tick):return ''
    observed={p['thingId'] for p in rt.batch.native.get('pawns',{}).get('pawns',[])
        if p.get('thingId') and p.get('dead') is False and p.get('downed') is True
        and p.get('drafted') is False and (p.get('health') or {}).get('stableRestEligible') is True}
    return ','.join(sorted(observed & set(facts.get('restingPatients',[]))))


async def manage_care(rt, facts, people):
    from .colony_skills import SkillBlocked, native
    state = facts['longTermMedical']
    goal = rt.current_plan.colony_goals['MaintainMedicalCare']
    goal.evidence['health'] = state
    if state['unknown']:
        raise SkillBlocked('Long-term health observations unavailable for '+', '.join(state['unknown']))
    actions, blockers = [], []
    for patient in state['patients']:
        if not patient['needsRest']:
            continue
        pawn = next(p for p in people if p['thingId'] == patient['patient'])
        if patient['care'] is None:
            blockers.append(patient['patient']+': medical care policy is unknown')
            continue
        if patient['care'] == 'NoCare':
            blockers.append(patient['patient']+': player medical policy disables care')
            continue
        if pawn.get('drafted') or pawn.get('mentalState'):
            blockers.append(patient['patient']+': drafted or unavailable for recovery')
            continue
        work = (pawn.get('work') or {}).get('types', [])
        for name in ('Patient', 'PatientBedRest'):
            entry = next((w for w in work if w.get('name') == name), None)
            if not entry or entry.get('disabled') is not False:
                blockers.append(patient['patient']+': '+name+' unavailable')
                continue
            if entry.get('priority', 0) > 0:
                continue
            overrides = rt.current_plan.control.get('work_overrides', {}).get(patient['patient'], {})
            if name in overrides:
                blockers.append(patient['patient']+': player disabled '+name)
                continue
            actions.append(native('home/pawn_config', pawn=patient['patient'], work=name+'=1', watch=False))
    goal.evidence['care_blockers'] = blockers
    if blockers:
        raise SkillBlocked('; '.join(blockers))
    if actions:
        method = 'rest-'+fingerprint(actions)[:16]
        if not goal.method_seen(method):
            return method, actions[:8]
        raise SkillBlocked('Recovery work settings changed after admission; inspect player work policy')
    # Ordinary patient/bed-rest jobs own bed choice. Chronic conditions remain
    # visible without inventing an operation or claiming the condition cured.
    goal.evidence['monitoring'] = True
    return None
