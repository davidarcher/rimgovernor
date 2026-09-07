import json
from pathlib import Path
import pytest
from rimbot.catalog import Catalog
from rimbot.capabilities import attach_execution_policy
from rimbot.planner import domains_for
from rimbot.contracts import Action,Proposal


def test_every_exposed_write_has_explicit_owner():
    catalog=Catalog()
    catalog.install_contracts(json.loads((Path(__file__).parents[1]/'controller/rimbot/data/construction_contracts.json').read_text()))
    for entry in catalog.entries.values():
        if entry['write'] and entry['exposed']:
            assert entry['execution_domains'] or entry['controller_owner'],entry['name']
    assert catalog.entries['planning_create']['controller_owner']=='architect'
    assert not catalog.entries['post_pawn_edit_apparel']['exposed']
    assert catalog.entries['post_pawn_edit_apparel']['withheld_reason']


def test_new_write_never_inherits_permission_from_similar_name():
    with pytest.raises(ValueError,match='no execution policy'):
        attach_execution_policy({'name':'post_buildings_bills_delete_everything','write':True,'exposed':True})
    assert 'post_buildings_bills_delete_everything' not in domains_for('Executor:production')


@pytest.mark.parametrize('endpoint,extra',[
    ('put_buildings_bill_update',{'repeat_count':3}),
    ('put_buildings_bill_suspend',{'suspended':True}),
    ('put_buildings_bill_reorder',{'offset':1}),
    ('delete_buildings_bill_remove',{}),
])
async def test_single_bill_lifecycle_reaches_production_validation(colony,endpoint,extra):
    rt,_=colony
    action=Action(title='Update existing bill',endpoint=endpoint,arguments={'building_id':42,'bill_id':7,**extra})
    result=rt.planner.validate_submission('Executor:production',Proposal(summary='Production',actions=[action]))
    assert result.actions[0].endpoint==endpoint
    assert endpoint in domains_for('Infrastructure')
    with pytest.raises(ValueError):
        rt.planner.validate_submission('Executor:storage',Proposal(summary='Wrong owner',actions=[action]))
