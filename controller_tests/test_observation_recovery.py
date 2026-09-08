from types import SimpleNamespace
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import PlanStep, StepProgress


def test_failed_read_does_not_invalidate_or_reissue_construction():
    step=PlanStep(id='wall',title='Wall',completion_criteria='Built',
        action={'kind':'place_buildings','placements':[{'def_name':'Wall','x':1,'z':1}]})
    progress=StepProgress(state='waiting',project_id='p',issued={'0':{'confirmed':True}})
    row=SimpleNamespace(id='p',state='blocked',evidence='Cannot verify: transient transport failure')
    rt=SimpleNamespace(current_plan=SimpleNamespace(spec=SimpleNamespace(steps=[step]),progress={'wall':progress}),
        projects=SimpleNamespace(rows=[row]),signal=lambda *a:None)
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state=='waiting' and progress.failure.code=='observation_unavailable'
    assert progress.issued=={'0':{'confirmed':True}}
    row.state='complete';row.evidence='Wall observed'
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state=='complete' and progress.failure is None
