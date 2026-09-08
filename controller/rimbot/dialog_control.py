"""Exact native window dismissal with fresh state verification."""


def require_clear_windows(snapshot):
    """Do not open a letter over an existing window of unknown ownership."""
    if snapshot.get('success') is not True or not isinstance(snapshot.get('windows'), list):
        raise ValueError('Current windows are unavailable; inspect UI before opening a letter')
    if snapshot['windows']:
        raise ValueError('A window is already open; inspect and resolve it before opening a letter')


def verify_letter_window(snapshot):
    windows = snapshot.get('windows')
    if (snapshot.get('success') is not True or not isinstance(windows, list) or not windows
            or any(not isinstance(w, dict) or type(w.get('id')) is not int
                   or not isinstance(w.get('type'), str) or not w['type'] for w in windows)):
        raise ValueError('Letter window opening is unverified; inspect before issuing another UI action')


def dismissal_target(target_id, snapshot):
    windows = snapshot.get('targets', {}).get('windows')
    if snapshot.get('success') is not True or not isinstance(windows, list):
        raise ValueError('Native window targets are unavailable')
    matches = [w for w in windows if isinstance(w, dict)
               and target_id and w.get('dismissTargetId') == target_id]
    if len(matches) != 1 or not isinstance(matches[0].get('id'), int) or not matches[0].get('type'):
        raise ValueError('Select an exact current dismissTargetId from get_screen_targets; other UI actions are not enabled')
    return matches[0]


def verify_window_dismissal(target_id, target, receipt, observed):
    if (receipt.get('success') is not True or receipt.get('targetId') != target_id
            or receipt.get('targetKind') != 'window_dismiss'
            or receipt.get('actionKind') != 'dismiss_window'):
        raise ValueError('Native receipt did not confirm window dismissal')
    before = receipt.get('before', {}).get('windows')
    after = receipt.get('after', {}).get('windows')
    fresh = observed.get('windows')
    if (observed.get('success') is not True
            or not all(isinstance(rows, list) for rows in (before, after, fresh))):
        raise ValueError('Window dismissal readback is unavailable')
    def identities(rows):
        if any(not isinstance(w, dict) or not isinstance(w.get('id'), int)
               or not w.get('type') for w in rows):
            raise ValueError('Window identity is unavailable')
        return {(w['id'], w['type']) for w in rows}
    removed = identities(before) - identities(after)
    if removed != {(target['id'], target['type'])} or removed & identities(fresh):
        raise ValueError('Window closure was not confirmed by native readback')
