from copy import deepcopy
from rimgovernor.bridge_game import for_model, READS
from rimgovernor.vendor.companion_ui import slim_surface
import pytest
from types import SimpleNamespace
from unittest.mock import AsyncMock
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.store import Store


def label(text, x=0, y=0):
    return {'kind': 'label', 'label': text, 'screenRect':
            {'x': x, 'y': y, 'width': 80, 'height': 18}}


def test_companion_collapses_draw_duplicates_without_mutating_native_data():
    surface = {'surfaceTargetId': 'window:7', 'elements': [label('Hello'), label('Hello')]}
    original = deepcopy(surface)
    result = slim_surface(surface)
    assert surface == original
    assert len(result['rows']) == 1
    assert result['rows'][0]['text'] == 'Hello'
    assert result['rows'][0]['dupes'] == 1


def test_companion_reports_truncation_scroll_and_surface_identity():
    surface = {'surfaceTargetId': 'window:7', 'elements': [label('a' * 200),
        {'kind': 'scroll_view', 'scroll': {'canScrollY': True, 'offsetY': 5, 'maxOffsetY': 100}}]}
    result = for_model({'success': True, 'captureId': 12, 'surfaces': [surface]}, 'rimworld/get_ui_layout')
    assert result['success'] is True and result['captureId'] == 12
    row = result['surfaces'][0]
    assert row['id'] == 'window:7'
    assert row['rows'][0]['text'].endswith('...(+40 chars)')
    assert row['scroll'][0]['maxOffsetY'] == 100
    assert 'rimworld/get_ui_layout' in READS


def test_indeterminate_checkbox_is_not_reported_as_unchecked():
    checkbox = dict(label('All'), kind='checkbox', actionable=True, isChecked=None)
    result = slim_surface({'elements': [checkbox]})
    assert result['rows'][0]['checked'] is None


@pytest.mark.asyncio
@pytest.mark.parametrize('target', [None, {'load_token': 'old', 'actionable': True},
    {'load_token': 'current', 'actionable': True, 'disabled': True},
    {'load_token': 'current', 'actionable': False}])
async def test_ui_actions_reject_missing_stale_disabled_targets(tmp_path, target):
    store = Store(tmp_path/'ui.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.context_token = 'current'
    rt.sync_identity = AsyncMock()
    rt.game = SimpleNamespace(invoke=AsyncMock())
    if target:
        rt.ui_targets['control'] = target
    try:
        with pytest.raises(ValueError):
            await rt.native('rimworld/click_ui_target', {'targetId': 'control'})
        rt.game.invoke.assert_not_awaited()
    finally:
        store.close()
