from copy import deepcopy
from rimbot.bridge_game import for_model


def test_registry_definitions_survive_large_duplicate_ui_state():
    entry = dict(buildableDefName='ModdedWall', stuffDefName='ModdedTimber',
                 visible=True, disabled=True, disabledReason='Research required')
    payload = dict(success=True, designators=[entry], designatorCount=1,
                   state={'ui':'x'*25000}, designatorState={'duplicate':'y'*25000})
    original = deepcopy(payload)
    result = for_model(payload, tool='rimworld/list_architect_designators')
    assert result['designators'] == [entry] and result['designatorCount'] == 1
    assert result['catalog_page']['next_offset'] is None
    assert payload == original


def test_large_registry_is_not_silently_truncated():
    payload = dict(success=True, designators=[{'description':'x'*25000}])
    assert for_model(payload, tool='rimworld/list_architect_designators')['requires_narrower_query']


def test_other_tools_keep_state_evidence():
    payload = dict(state={'paused':True}, designatorState={'selected':'wall'})
    assert for_model(payload, tool='rimworld/get_selection_semantics') == payload


def test_large_catalog_can_be_traversed_without_lost_or_partial_entries():
    rows=[dict(id=str(i),description='x'*3000,buildableDefName=f'Modded{i}') for i in range(23)]
    payload=dict(success=True,designators=rows,designatorCount=len(rows))
    collected=[]; offset=0
    while offset is not None:
        page=for_model(payload,tool='rimworld/list_architect_designators',catalog_offset=offset)
        assert not page.get('requires_narrower_query')
        collected.extend(page['designators'])
        offset=page['catalog_page']['next_offset']
    assert collected == rows
    assert payload['designators'] == rows


def test_continuation_is_complete_and_preserves_native_filters():
    from rimbot.bridge_game import inspection_result
    payload={'designators':[{'id':str(i)} for i in range(10)]}
    args={'categoryId':'architect-category:structure','includeHidden':False}
    first=inspection_result(payload,'rimworld/list_architect_designators',args)
    call=first['catalog_page']['next_call']
    assert call == {'name':'inspect','arguments':{'name':'rimworld/list_architect_designators',
        'arguments':args,'catalog_offset':8}}
    next_args=call['arguments']
    last=inspection_result(payload,next_args['name'],next_args['arguments'],next_args['catalog_offset'])
    assert last['designators']==payload['designators'][8:]
    assert last['catalog_page']['next_offset'] is None
    assert 'next_call' not in last['catalog_page']
    next_args['arguments']['includeHidden']=True
    assert args['includeHidden'] is False
