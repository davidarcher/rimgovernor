from rimbot.contracts import Proposal
from rimbot.planner import ROLES

async def test_department_routing_and_main_model_fallback(colony):
    rt,_=colony
    assert all(rt.model_for_role(role) is rt.model for role in ROLES)
    await rt.configure(rt.settings.model_copy(update={'model':'test-main-model','manager_model':'qwen3.5-4b'}))
    assert rt.manager_model.settings.model=='qwen3.5-4b'
    assert all(rt.model_for_role(role) is rt.manager_model for role in ROLES)
    assert rt.model_for_role('Strategy: plan') is rt.model
    assert rt.model_for_role('Daily planning: plan') is rt.model
    assert rt.model_for_role('Administrator: reconcile') is rt.manager_model
    assert rt.model_for_role('Administrator: escalated review') is rt.model
    assert rt.model_for_role('Executor:construction') is rt.manager_model
    old=rt.manager_model
    await rt.configure(rt.settings.model_copy(update={'manager_model':''}))
    assert old.http.is_closed
    assert rt.manager_model is None
    assert rt.model_for_role('Executor:construction') is rt.model

async def test_planner_uses_department_client_and_logs_model(colony):
    rt,_=colony
    await rt.configure(rt.settings.model_copy(update={'model':'test-main-model','manager_model':'qwen3.5-4b'}))
    rt.cycle_generation=rt.generation
    async def small(*args):return {'role':'assistant','content':'{"summary":"No construction needed","actions":[]}'},{}
    async def main(*args):raise AssertionError('Department used the main model')
    rt.manager_model.complete=small;rt.model.complete=main
    result=await rt.planner.ask('Infrastructure',{},Proposal,False)
    assert result.actions==[]
    calls=[e for e in rt.store.history(rt.colony,100,include_diagnostics=True) if e['kind']=='model_call']
    assert calls[-1]['model']=='qwen3.5-4b'

async def test_task_planner_uses_small_model_with_reasoning(colony):
    rt,_=colony
    await rt.configure(rt.settings.model_copy(update={'model':'test-main-model','manager_model':'qwen3.5-4b','reasoning':True}))
    rt.cycle_generation=rt.generation
    async def small(messages,tools,thinking,progress):
        assert thinking is True
        return {'role':'assistant','content':'{"summary":"No new orders","actions":[]}'},{}
    async def main(*args):raise AssertionError('Task planner used main model')
    rt.manager_model.complete=small;rt.model.complete=main
    await rt.planner.ask('Executor:construction',{},Proposal,rt.settings.reasoning)
    calls=[e for e in rt.store.history(rt.colony,100,include_diagnostics=True) if e['kind']=='model_call']
    assert calls[-1]['model']=='qwen3.5-4b'
