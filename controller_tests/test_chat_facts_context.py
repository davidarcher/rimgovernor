import json
import pytest
from rimbot.planner import facts_index,inspect_facts


def test_large_native_catalog_is_discoverable_without_inlining_it():
    facts={'definitions':{f'Building{i}':{'costs':{'Steel':100},'description':'x'*300} for i in range(500)},
        'resources':{'Steel':250},'cells':[{'x':1,'z':2}]}
    index=facts_index(facts)
    assert len(json.dumps(index))<500
    assert index['available_sections']==['definitions','resources']
    result=inspect_facts(facts,['resources'])
    assert result['sections']=={'resources':{'Steel':250}}
    assert result['historical'] is True
    assert inspect_facts(facts,['definitions'])['sections']['definitions']==facts['definitions']
    assert len(facts['definitions'])==500


@pytest.mark.parametrize('sections',[[],['cells'],['missing'],['a','b','c','d']])
def test_fact_inspection_rejects_unknown_spatial_or_unbounded_requests(sections):
    with pytest.raises(ValueError):inspect_facts({'a':1,'b':2,'c':3,'d':4,'cells':[]},sections)
