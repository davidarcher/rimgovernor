from copy import deepcopy

import pytest

from rimgovernor.bridge_game import for_model


@pytest.mark.parametrize('tool,rows', [
    ('home/list_buildings', {'buildings':[{'thingId':'Cooler1','rotation':'East',
        'thermalSides':{'readable':True,'sides':[{'side':'exhaust','position':{'x':2,'z':1},
            'fogged':True,'impassable':None}]}}],
        'powerSummary':{'readable':False,'error':'unavailable'},'powerNets':None}),
    ('home/bills', {'benches':[{'thingId':'Bench1','bills':[{'index':0,
        'canRunNow':False,'blockedBy':['Ingredient scan unavailable'],'ingredients':None}]}]}),
    ('home/list_zones', {'zones':[{'id':3,'hidden':True,'cellsNotListed':8,
        'gridCellsNotListed':8,'filter':{'allowedDefCount':None},'cellsOccupied':None}],
        'anomalies':['Zone grid disagreement']}),
    ('home/list_pawns', {'pawns':[{'thingId':'Pawn1','health':None,
        'work':{'applies':False},'schedule':{'applies':False},
        'relations':{'opinionMethod':'reconstructed','relations':[]}}],'pawnsFiltered':7}),
    ('rimworld/list_alerts', {'alerts':[],'totalCount':2,'returnedCount':0,'truncated':True}),
])
def test_compact_inspectors_preserve_ids_unknowns_omissions_and_scope(tool, rows):
    payload=dict(success=True,notes={'visibility':'Only visible conditions; absence is not safety'},
                 filters={'match':'selected','health':True},skipped={'byMatch':12},**rows)
    original=deepcopy(payload)
    payload['operation']={'transport':'metadata'}
    assert for_model(payload,tool)==original
    assert payload==dict(original,operation={'transport':'metadata'})


def test_oversized_inspector_requires_narrowing_without_presenting_partial_facts():
    payload={'success':True,'pawns':[{'thingId':'Pawn1','health':{'detail':'x'*25000}}]}
    result=for_model(payload,'home/list_pawns')
    assert result['requires_narrower_query'] is True
    assert 'pawns' not in result
