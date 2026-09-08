"""Read native patient state; accepting an order is not finishing treatment."""
from .colony_plan import Failure


def rescue_outcome(arguments, pawns):
    patient = next((p for p in pawns if p.get('thingId') == arguments['target']), None)
    rescuer = next((p for p in pawns if p.get('thingId') == arguments['pawn']), None)
    if patient is not None and patient.get('dead') is True:
        return Failure(code='patient_dead', detail='Patient died; rescue did not complete.')
    if patient is not None and patient.get('dead') is False:
        health = patient.get('health') or {}
        if health.get('inBed') is True and health.get('bedThingId'):
            return 'complete'
    # Carried pawns disappear from the spawned-colonist roster. That is transit,
    # not death or successful delivery; require the exact carried identity.
    if rescuer is None or rescuer.get('dead') or rescuer.get('downed'):
        return Failure(code='rescuer_unavailable', detail='Rescuer unavailable; delivery is unverified.')
    if rescuer.get('job') == 'Rescue':
        if rescuer.get('carriedThingId') == arguments['target'] or patient is not None:
            return 'waiting'
    if patient is None:
        return Failure(code='patient_unobserved', detail='Patient is neither observed in a bed nor carried by the rescuer.')
    return Failure(code='rescue_interrupted', detail='Patient has not reached a bed and the rescue order is no longer progressing.', retryable=True)


def patient_outcome(arguments, pawns):
    patient = next((p for p in pawns if p.get('thingId') == arguments['target']), None)
    doctor = next((p for p in pawns if p.get('thingId') == arguments['pawn']), None)
    if patient is None:
        return Failure(code='patient_unobserved', detail='Patient is absent from the current colony observation; treatment is unverified.')
    if patient.get('dead') is True:
        return Failure(code='patient_dead', detail='Patient died; treatment did not complete.')
    health = patient.get('health') or {}
    if patient.get('dead') is not False or not isinstance(health.get('needsTend'), bool):
        return Failure(code='medical_state_unknown', detail='Patient health could not be read; treatment is unverified.')
    if health['needsTend'] is False:
        return 'complete'
    if doctor is None or doctor.get('dead') or doctor.get('downed'):
        return Failure(code='doctor_unavailable', detail='Patient still needs tending and the assigned doctor is unavailable.')
    if doctor.get('job') != 'TendPatient':
        return Failure(code='tending_interrupted', detail='Patient still needs tending, but the assigned doctor is no longer tending.',
            retryable=True, evidence={'doctor_job': doctor.get('job')})
    return 'waiting'
