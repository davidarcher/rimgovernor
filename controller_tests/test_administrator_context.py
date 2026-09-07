from copy import deepcopy
from rimbot.decision_context import administrator_context
import pytest


@pytest.mark.parametrize('name,role',[('Plans','Strategy: colony'),('DailyPlan','Daily planning: colony')])
async def test_planning_uses_compact_projection_and_keeps_agency(colony,name,role):
    import json
    from unittest.mock import AsyncMock
    from rimbot import contracts
    from rimbot.model import ModelError
    rt,_=colony
    context={'player_direction':['Protect the farm'],'world_facts':{'food_runway_days':0},
             'resource_overview':{'terrain':{'items':[{'def_name':'Soil','fertility':1,'nearby_cells':30,'nearest_cell':{'x':2,'z':3}}]},
                                  'food_crops':{'items':[{'def_name':'Crop','min_fertility':.7,'nearby_fertility_eligible_cells':30}]}},
             'construction_work':{'total':0,'sites':[]}}
    model=rt.model_for_role(role)
    model.complete=AsyncMock(side_effect=ModelError('capture'))
    with pytest.raises(ModelError,match='capture'):
        await rt.planner.ask(role,context,getattr(contracts,name))
    sent=json.loads(model.complete.call_args.args[0][1]['content'])
    assert 'crop_land_comparison' not in sent and 'construction_work' not in sent
    assert sent['world_facts']==context['world_facts']
    assert sent['player_direction']==context['player_direction']
    assert sent['resource_overview']['food_crops']==context['resource_overview']['food_crops']
    tools={t['function']['name'] for t in model.complete.call_args.args[1]}
    assert {'query','execute_order','memory_read','wiki_search'} <= tools


def test_arbitration_preserves_proposals_budgets_and_crop_choices():
    context = {'proposals': {'Survival:0': {'objective': {'kind': 'growing', 'constraints': ['No expansion']}}},
               'projects': [{'project_id': 'existing', 'after_projects': ['supplies']}],
               'construction_budget': {'available': {'WoodLog': 20}},
               'world_facts': {'food': {'food_runway_days': 0}},
               'resource_overview': {'food_crops': {'items': [{'def_name': 'Crop', 'min_fertility': .7,
                                                             'nearby_fertility_eligible_cells': 0}]},
                                     'animals': {'items': [{'def_name': 'Horse', 'owned_count': 1,
                                                           'location': {'sample_ids': [100], 'nearby_count': 1, 'nearest_distance': 3}}]}},
               'crop_land_comparison': {'crops': []},
               'strategy_guidance': [{'id': 'pen'}, {'id': 'first-days'}, {'id': 'sleeping'}]}
    before = deepcopy(context)
    result = administrator_context(context)
    assert context == before
    for key in ('proposals', 'projects', 'construction_budget', 'world_facts'):
        assert result[key] == context[key]
    assert result['resource_overview']['food_crops'] == context['resource_overview']['food_crops']
    assert result['resource_overview']['animals']['items'][0]['owned_count'] == 1
    assert result['strategy_guidance'] == [{'id': 'first-days'}]
    assert result['strategy_guidance_omitted'] == 2


def test_department_concerns_are_losslessly_factored_and_source_unchanged():
    import copy
    context={'proposals':{str(i):{'owner':'Food','objective':{'kind':'growing','crop_def':'Crop','target_cells':20,'quantity':None},'blockers':['Short runway','No harvest yet']} for i in range(4)}}
    original=copy.deepcopy(context)
    result=administrator_context(context)
    assert len(result['department_concerns'])==1
    for candidate in result['proposals'].values():
        assert result['department_concerns'][candidate['blockers_ref']]==['Short runway','No harvest yet']
        assert candidate['objective']['target_cells']==20
    assert context==original
