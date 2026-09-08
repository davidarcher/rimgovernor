from copy import deepcopy
from rimbot.bridge_game import for_model


def test_registry_definitions_survive_large_duplicate_ui_state():
    entry = dict(buildableDefName='ModdedWall', stuffDefName='ModdedTimber',
                 visible=True, disabled=True, disabledReason='Research required')
    payload = dict(success=True, designators=[entry], designatorCount=1,
                   state={'ui':'x'*25000}, designatorState={'duplicate':'y'*25000})
    original = deepcopy(payload)
    result = for_model(payload, tool='rimworld/list_architect_designators')
    assert result == dict(success=True, designators=[entry], designatorCount=1)
    assert payload == original


def test_large_registry_is_not_silently_truncated():
    payload = dict(success=True, designators=[{'description':'x'*25000}])
    assert for_model(payload, tool='rimworld/list_architect_designators')['requires_narrower_query']


def test_other_tools_keep_state_evidence():
    payload = dict(state={'paused':True}, designatorState={'selected':'wall'})
    assert for_model(payload, tool='rimworld/get_selection_semantics') == payload
