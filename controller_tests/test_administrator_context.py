from copy import deepcopy
from rimbot.decision_context import administrator_context


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
