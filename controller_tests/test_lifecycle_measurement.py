from types import SimpleNamespace

from rimbot.colony_plan import ColonyPlan, CommitSteps, PlanStep, StepProgress
from rimbot.plan_archive import bind_archive, finish_archive, prepare_archive
from rimbot.store import Store
from scripts.lifecycle_measurement import ledger_sample


def test_native_recovery_measurement_survives_live_to_archive_transition(tmp_path):
    path = tmp_path / 'state.sqlite'
    store = Store(path)
    plan = ColonyPlan()
    step = PlanStep(id='recovered', title='Recovered work', source='AUTOPILOT',
        goal_id='EnsureWorkAssignments', completion_criteria='Native receipt',
        action={'kind': 'native_operation', 'tool': 'home/pawn_config',
                'arguments': {'pawn': 'Thing_Human1', 'work': 'Growing=1', 'dryRun': False}})
    plan.commit(CommitSteps(expected_revision=plan.revision, reason='fixture', steps=[step]).decision(plan),
                actor='strategist', tick=1)
    plan.progress[step.id] = StepProgress(state='complete',
        issued={'0': {'confirmed': True, 'nested': {'native_id': 'receipt'}}},
        recovery_history=[{'failure': {'code': 'construction_resources'}, 'tick': 1}])
    rt = SimpleNamespace(current_plan=plan, store=store, colony='colony', counters={'actions': 1},
                         batch=SimpleNamespace(summary=SimpleNamespace(end_tick=2)))
    before = ledger_sample(rt, path)
    plan.progress[step.id].issued['0']['nested']['native_id'] = 'later mutation'
    assert before['actions']['recovered']['issued']['0']['nested']['native_id'] == 'receipt'
    for index in range(72):
        replacement = step.model_copy(update={'id': 'next-'+str(index), 'action': step.action.model_copy(
            update={'arguments': dict(step.action.arguments, pawn='Thing_Human'+str(index+2))})})
        plan.commit(CommitSteps(expected_revision=plan.revision, reason='next', steps=[replacement]).decision(plan),
                    actor='strategist', tick=index+3)
    bind_archive(plan, store, rt.colony)
    snapshot, records, methods, evidence = prepare_archive(plan)
    store.archive_and_set(rt.colony, 'plan', snapshot, records, methods, evidence)
    finish_archive(plan, snapshot, records, methods, evidence)
    after = ledger_sample(rt, path)
    assert after['recovery_histories'] == before['recovery_histories']
    assert after['archived_recovery_entries'] == 1
    assert after['archive_hashes']['retired_actions']
    assert ledger_sample(rt, path)['archive_hashes'] == after['archive_hashes']
    store.close()
