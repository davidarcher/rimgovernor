from copy import deepcopy

import pytest

from rimbot.bridge_game import for_model


@pytest.mark.parametrize('tool', [
    'home/list_buildings', 'home/list_zones', 'home/bills',
    'home/pawn', 'rimworld/list_alerts',
])
def test_model_inspection_keeps_native_scope_and_unknowns(tool):
    payload = {
        'notes': {'reachability': 'Not evaluated for an individual pawn.',
                  'visibility': 'Only currently visible entities are included.'},
        'filters': {'includeHidden': False},
        'rows': [{'id': 'native:7', 'available': None, 'visible': False}],
        'omittedCount': 3,
        'operation': 'read', 'watch': None,
    }
    original = deepcopy(payload)
    result = for_model(payload, tool)
    assert result == {k: v for k, v in original.items()
                      if k not in ('operation', 'watch')}
    assert payload == original


def test_large_scope_notes_require_narrowing_instead_of_losing_caveats():
    result = for_model({'rows': [{'id': 'native:7'}],
                        'notes': {'scope': 'x' * 25000}}, 'home/list_buildings')
    assert result['requires_narrower_query'] is True
    assert 'rows' not in result
    assert result['fields']['notes'] == 1


def test_explicitly_unavailable_native_notes_remain_unknown():
    assert for_model({'notes': None}, 'rimworld/list_alerts') == {'notes': None}
