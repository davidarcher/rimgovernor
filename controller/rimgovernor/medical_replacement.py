"""Replace a confirmed unavailable provider without rewriting its action/receipt."""
from copy import deepcopy
from .colony_plan import CommitSteps, PlanStep, PlanSpec
from .config import ModelRole
from .medical_triage import treatment_pairs
from .native_contracts import validate_native_steps


async def replace_doctor(rt, step, pawns, *, tick, token, direction, revision, limit):
    plan = rt.current_plan
    if step.source != 'AUTOPILOT' or step.goal_id != 'CriticalMedical' or step.action.kind != 'native_operation' or step.action.completion != 'patient_tended':
        return None
    progress = plan.progress[step.id]
    goal = plan.colony_goals[step.goal_id]
    receipt = progress.issued.get('0', {})
    patient = next((p for p in pawns if p.get('thingId') == step.action.arguments['target']), {})
    doctor = next((p for p in pawns if p.get('thingId') == step.action.arguments['pawn']), {})
    if (progress.state != 'blocked' or not progress.failure or progress.failure.code not in
            ('doctor_unavailable', 'tending_interrupted') or goal.cancelled
            or receipt.get('confirmed') is not True or receipt.get('load_token') != token
            or type(receipt.get('issued_tick')) is not int or tick < receipt['issued_tick']
            or type(receipt.get('player_direction')) is not int
            or receipt['player_direction'] != plan.control.get('player_direction', 0)
            or patient.get('dead') is not False or (patient.get('health') or {}).get('needsTend') is not True):
        return None
    for key, person in (('order_generation', doctor), ('patient_order_generation', patient)):
        if type(receipt.get(key)) is not int or person.get('orderGeneration') != receipt[key]:
            return None
    unavailable = doctor.get('downed') is True or doctor.get('dead') is True or bool(doctor.get('mentalState'))
    unavailable |= bool(doctor.get('work')) and not any(w.get('name') == 'Doctor' and w.get('disabled') is False
                                                       for w in doctor['work'].get('types', []))
    if not unavailable or any(p.get('job') == 'TendPatient' for p in pawns):
        return None
    count = goal.evidence.get('doctor_replacements', 0)
    if count >= limit:
        return 'Doctor replacement limit reached; medical hold retained.'
    candidates = treatment_pairs(pawns, [patient['thingId']], plan.control)
    for _, identity in list(candidates)[:8]:
        action = step.action.model_copy(deep=True)
        action.arguments['pawn'] = identity
        preview = await rt.game.invoke(action.tool, dict(action.arguments, dryRun=True), allow_write=False)
        if preview.get('success') is not True:
            continue
        replacement = PlanStep.model_validate(dict(step.model_dump(),
            id=f'medical-replacement-{plan.revision}-{count}', action=action.model_dump(), after=[]))
        if replacement.signature() in plan.cancelled_actions:
            continue
        await validate_native_steps(PlanSpec(steps=[replacement]), rt.game)
        await rt.sync_identity()
        await rt.refresh_clock_events()
        if (rt.current_plan is not plan or plan.revision != revision or rt.context_token != token
                or rt.chat_revision != direction or rt.mode != 'automate'):
            return None
        decision = CommitSteps(expected_revision=revision, reason='Replace unavailable native doctor',
                               steps=[replacement]).decision(plan)
        plan.commit(decision, actor=ModelRole.STRATEGIST, tick=tick)
        # Supersede this attempt without permanently banning the provider/patient
        # pair from a later medical episode, as a player cancellation would.
        progress.state = 'cancelled'
        plan.cancelled_ids.append(step.id)
        goal.steps.append(replacement.id)
        goal.status, goal.reason = 'active', 'Replacement doctor accepted; treatment remains unverified.'
        goal.evidence['doctor_replacements'] = count + 1
        goal.evidence.setdefault('replaced_treatment', []).append(dict(step=step.id,
            replacement=replacement.id, receipt=deepcopy(receipt), failure=progress.failure.model_dump()))
        goal.last_progress_tick = tick
        return goal.reason
    return 'No native-approved replacement doctor; medical hold retained.'
