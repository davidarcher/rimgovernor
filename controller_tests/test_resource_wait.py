from types import SimpleNamespace
from unittest.mock import Mock,AsyncMock
from rimbot.resource_budget import resource_review_due,balances
from rimbot.project_schedule import evidence,REVIEW_TICKS
from rimbot.semantic import retain_project
from rimbot.semantic_models import WorkObjective


def setup():
    project={'project_id':'p','kind':'construction','outcome':'Build shelter','work_ids':[],
             'status':'needs_review','progress':{},'quantity':1}
    rt=SimpleNamespace(memory={'work':[],'projects':[project]},last_tick=100,note=Mock(),persist=Mock())
    project['resource_request']={'costs':{'TestWood':20},'shortage':{'TestWood':10},'observed_tick':100,'evidence':evidence(project,rt.memory)}
    return rt,project


async def test_unchanged_shortage_waits_and_added_supplies_reopen(monkeypatch):
    rt,project=setup()
    survey=AsyncMock(return_value=SimpleNamespace(materials=balances({'TestWood':10},{},{})))
    monkeypatch.setattr('rimbot.resource_budget.observe_budget',survey)
    assert not await resource_review_due(rt,project)
    assert not await resource_review_due(rt,project)
    rt.note.assert_not_called()
    project['execution_review']={'tick':100}
    survey.return_value=SimpleNamespace(materials=balances({'TestWood':20},{},{}))
    assert await resource_review_due(rt,project)
    assert 'execution_review' not in project
    assert project['resource_request']['shortage']=={}


async def test_commitments_and_reserves_still_limit_spendable_supply(monkeypatch):
    rt,project=setup()
    monkeypatch.setattr('rimbot.resource_budget.observe_budget',AsyncMock(return_value=SimpleNamespace(materials=balances({'TestWood':30},{'TestWood':15},{'TestWood':5}))))
    assert not await resource_review_due(rt,project)
    assert project['resource_request']['shortage']=={'TestWood':10}


async def test_changed_work_force_and_periodic_review_bypass_old_cost(monkeypatch):
    rt,project=setup()
    survey=AsyncMock(side_effect=AssertionError('Old cost must not gate reconsideration'))
    monkeypatch.setattr('rimbot.resource_budget.observe_budget',survey)
    assert await resource_review_due(rt,project,force=True)
    rt.last_tick=100+REVIEW_TICKS
    assert await resource_review_due(rt,project)
    rt.last_tick=101;project['progress']={'sites':[{'state':'ready_for_work'}]}
    assert await resource_review_due(rt,project)
    survey.assert_not_awaited()


def test_revising_objective_discards_old_batch_cost():
    memory={}
    project=retain_project(memory,'Infrastructure',WorkObjective(kind='construction',outcome='Shelter',success_signals=['Roofed']))
    project['resource_request']={'costs':{'TestWood':20}}
    retain_project(memory,'Infrastructure',WorkObjective(project_id=project['project_id'],kind='construction',outcome='Shelter',quantity=2,success_signals=['Roofed']))
    assert 'resource_request' not in project


async def test_blocked_project_skips_executor_integration(colony,monkeypatch):
    from rimbot.semantic import execute_projects
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    project=retain_project(rt.memory,'Infrastructure',WorkObjective(kind='construction',outcome='Shelter',success_signals=['Roofed']))
    project['progress']={}
    project['resource_request']={'costs':{'TestWood':20},'shortage':{'TestWood':10},'observed_tick':rt.last_tick,'evidence':evidence(project,rt.memory)}
    rt.memory['spatial_layout']={'zones':[{'id':'z'}]}
    monkeypatch.setattr('rimbot.spatial.prepare_layout',AsyncMock())
    monkeypatch.setattr('rimbot.spatial.release_retired',AsyncMock())
    monkeypatch.setattr('rimbot.project_progress.refresh_progress',AsyncMock(return_value={}))
    monkeypatch.setattr('rimbot.resource_budget.observe_budget',AsyncMock(return_value=SimpleNamespace(materials=balances({'TestWood':10},{},{}))))
    rt.manager_context=AsyncMock(return_value={});rt.observe_resources=AsyncMock(return_value={})
    rt.planner.ask=AsyncMock(side_effect=AssertionError('Unchanged shortage must not invoke the executor'))
    await execute_projects(rt,{},[project])
    rt.planner.ask.assert_not_awaited()
    assert project['execution_skip_reason']=='Waiting for changed material availability'
