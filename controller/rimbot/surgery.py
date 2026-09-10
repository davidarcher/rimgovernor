"""Surgery admission and health postconditions for explicit player operations."""
from .colony_plan import Failure


def recovery_patients(rt):
    """Short, direction-scoped permission; the native clock still checks health."""
    result = set()
    for step in rt.current_plan.spec.steps:
        if (step.source != 'PLAYER' or step.action.kind != 'native_operation'
                or step.action.completion != 'surgery_health'):
            continue
        progress = rt.current_plan.progress[step.id]
        receipt = progress.issued.get('0', {})
        if (progress.state == 'cancelled' or receipt.get('confirmed') is not True
                or any(step.action.arguments[k] != rt.identity.get(k) for k in ('colonyId', 'loadToken', 'mapId'))
                or receipt.get('player_direction') != rt.current_plan.control.get('player_direction', 0)
                or type(receipt.get('issued_tick')) is not int
                or not 0 <= rt.batch.summary.end_tick - receipt['issued_tick'] < rt.controller.policy.blocked_after_ticks):
            continue
        result.add(step.action.arguments['patient'])
    return ','.join(sorted(result))


def effect_from_preview(preview):
    selected = preview.get('selected') or {}
    if preview.get('success') is not True or selected.get('supported') is not True:
        raise ValueError('Native surgery refused: '+str(preview.get('error') or 'unsupported health effect'))
    effect = {k: selected.get(k) for k in ('recipe', 'part', 'addsHediff', 'removesHediff')}
    if not effect['addsHediff'] and not effect['removesHediff']:
        raise ValueError('Surgery has no supported native health postcondition')
    return effect


def surgery_outcome(action, receipt, pawns):
    patient = next((p for p in pawns if p.get('thingId') == action.arguments['patient']), None)
    if patient is None:
        return Failure(code='patient_unobserved', detail='Surgical patient is not observed; outcome remains unverified')
    if patient.get('dead') is True:
        return Failure(code='patient_dead', detail='Surgical patient died; operation did not achieve recovery')
    health = patient.get('health') or {}
    if (patient.get('dead') is not False or health.get('careObservationVersion') != 1
            or not isinstance(health.get('hediffs'), list) or not isinstance(health.get('surgeryBills'), list)):
        return Failure(code='medical_state_unknown', detail='Surgical health or bill observation unavailable')
    effect = action.medical_effect
    part = None if effect['part'] == -1 else effect['part']
    before = {entry.split(':')[0] for entry in action.arguments['expectedHealth'].split(';')}
    matching = [h for h in health['hediffs'] if h.get('partIndex') == part]
    added = not effect['addsHediff'] or any(h.get('defName') == effect['addsHediff']
        and h.get('id') and h['id'] not in before for h in matching)
    removed = not effect['removesHediff'] or not any(h.get('defName') == effect['removesHediff'] for h in matching)
    # A finished bill alone also occurs on cancellation and surgical failure.
    if added and removed:
        return 'complete'
    if not receipt.get('bill_id'):
        return Failure(code='surgery_uncertain', detail='No confirmed operation bill identity; observe health before any new request')
    bill = next((b for b in health['surgeryBills'] if b.get('id') == receipt['bill_id']), None)
    if bill is None:
        return Failure(code='surgery_failed_or_cancelled', detail='Operation bill disappeared without the expected health change; no automatic retry')
    if bill.get('suspended') is not False:
        return Failure(code='surgery_suspended', detail='Patient operation is suspended; preserve player bill settings')
    return 'waiting'
