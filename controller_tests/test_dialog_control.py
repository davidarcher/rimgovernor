import pytest
from rimbot.dialog_control import dismissal_target, verify_window_dismissal
from rimbot.bridge_game import READS, is_write

WINDOW = {'id': 7, 'type': 'Dialog', 'dismissTargetId': 'exact'}


def test_only_current_native_dismiss_target_is_accepted():
    snapshot = {'success': True, 'targets': {'windows': [WINDOW]}}
    assert dismissal_target('exact', snapshot) == WINDOW
    for target in (None, '', 'stale', 'context-menu-option:1:1'):
        with pytest.raises(ValueError):
            dismissal_target(target, snapshot)
    with pytest.raises(ValueError):
        dismissal_target('exact', {'success': True, 'targets': {'windows': [WINDOW, WINDOW]}})


def receipt():
    return {'success': True, 'targetId': 'exact', 'targetKind': 'window_dismiss',
            'actionKind': 'dismiss_window', 'before': {'windows': [WINDOW]},
            'after': {'windows': []}}


def test_verified_closure_requires_exact_identity_and_fresh_absence():
    verify_window_dismissal('exact', WINDOW, receipt(), {'success': True, 'windows': []})
    for state in ({}, {'success': True, 'windows': [WINDOW]},
                  {'success': True, 'windows': [{}]}):
        with pytest.raises(ValueError):
            verify_window_dismissal('exact', WINDOW, receipt(), state)
    wrong = receipt()
    wrong['before']['windows'] = [{'id': 8, 'type': 'Dialog'}]
    with pytest.raises(ValueError):
        verify_window_dismissal('exact', WINDOW, wrong, {'success': True, 'windows': []})


def test_ui_reads_and_writes_are_separate():
    assert {'rimworld/get_ui_state', 'rimworld/get_screen_targets'} <= READS
    assert is_write('rimworld/click_screen_target', {'targetId': 'exact'})
