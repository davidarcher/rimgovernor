"""Bounded recovery of confirmed treatment interruptions, never uncertain writes."""
from copy import deepcopy
from .colony_plan import NativeOperation
from .medical_outcome import patient_outcome


def recover_treatment(plan, step, pawns, *, token, tick, owners, limit):
    progress = plan.progress[step.id]
    goal = plan.colony_goals.get(step.goal_id)
    action = step.action
    if (step.source != 'AUTOPILOT' or step.goal_id != 'CriticalMedical'
            or not goal or goal.cancelled or progress.state != 'blocked'
            or not isinstance(action, NativeOperation) or action.completion != 'patient_tended'
            or not progress.failure or progress.failure.code != 'tending_interrupted'
            or not progress.failure.retryable):
        return None
    receipt = progress.issued.get('0', {})
    if (receipt.get('confirmed') is not True or receipt.get('load_token') != token
            or type(receipt.get('issued_tick')) is not int or tick < receipt['issued_tick']):
        return 'Treatment recovery requires a confirmed order from this load without a save rewind.'
    outcome = patient_outcome(action.arguments, pawns)
    if outcome == 'complete':
        progress.state, progress.failure = 'complete', None
        return 'Patient no longer needs tending; no replacement order was sent.'
    if outcome == 'waiting':
        progress.state, progress.failure = 'waiting', None
        return 'Existing treatment is progressing; no replacement order was sent.'
    if outcome.code != 'tending_interrupted':
        return outcome.detail
    if len(progress.recovery_history) >= limit:
        return f'Treatment recovery limit reached ({limit}); inspect the patient and doctor.'
    doctor_id = action.arguments['pawn']
    doctor = next(p for p in pawns if p.get('thingId') == doctor_id)
    if (doctor_id in plan.control.get('player_draft_overrides', {})
            or plan.control.get('work_overrides', {}).get(doctor_id, {}).get('Doctor') == 0
            or (doctor.get('drafted') is not False and owners.get(doctor_id) != token)):
        return 'Treatment recovery is held by player control of the assigned doctor.'
    if (not isinstance(doctor.get('job'), str) or doctor.get('mentalState')
            or doctor.get('dead') is not False or doctor.get('downed') is not False
            or not any(w.get('name') == 'Doctor' and w.get('disabled') is False
                       for w in (doctor.get('work') or {}).get('types', []))):
        return 'Assigned doctor is not currently confirmed capable of treatment.'
    if any(p.get('job') == 'TendPatient' for p in pawns):
        return 'Another treatment job is active; inspect its patient before issuing competing treatment.'
    progress.recovery_history.append({'tick': tick, 'load_token': token,
        'failure': progress.failure.model_dump(), 'issued': deepcopy(progress.issued),
        'doctor_job': doctor.get('job')})
    progress.issued = {}
    progress.state, progress.failure = 'pending', None
    return f'Retrying interrupted treatment ({len(progress.recovery_history)}/{limit}) after fresh patient and doctor checks.'
