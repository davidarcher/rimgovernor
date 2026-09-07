"""Observed project evidence, deliberately separate from order receipts.

These measurements do not interpret free-text success criteria or retire projects.
Missing observations stay unknown, and later observations can invalidate evidence.
"""


def assess(project):
    progress = project.get('progress', {})
    checks = []

    def add(label, actual, target):
        checks.append({'label': label, 'observed': actual, 'target': target,
                       'status': 'unknown' if actual is None else 'met' if actual >= target else 'unmet'})

    if project.get('kind') == 'growing':
        crop, target = project.get('crop_def'), project.get('target_cells')
        if crop and target:
            add('Crop zone cells', progress.get('matching_cells'), target)
            rows = [z for z in progress.get('zones', []) if z.get('crop') == crop]
            observed = (sum(z['plants_present'] for z in rows)
                        if 'zones' in progress and all(z.get('plants_present') is not None for z in rows) else None)
            add('Plants present', observed, target)
    if project.get('kind') == 'construction':
        roof = progress.get('roof', {})
        target = roof.get('interior_cells', 0)
        if target:
            add('Planned interior roofed', roof.get('roofed_cells')
                if roof.get('observed_cells') == target else None, target)
        for index, room in enumerate(progress.get('enclosures', []), 1):
            for field, label in [('enclosed', 'Enclosed'), ('reachable', 'Reachable'), ('roofed', 'Fully roofed')]:
                value = room.get(field)
                add(f'Room {index}: {label}', None if value is None else int(value), 1)

    # A met measurement is narrower than "the goal is achieved": e.g. plants
    # present are neither a harvest nor adequate nutrition, a roof isn't a room.
    return {'observed_tick': progress.get('observed_tick'), 'checks': checks,
            'status': ('unmet' if any(c['status'] == 'unmet' for c in checks)
                       else 'unknown' if not checks or any(c['status'] == 'unknown' for c in checks)
                       else 'measured_targets_met'),
            'review_required': list(project.get('success_signals', [])),
            'meaning': 'Measurements only. Free-text success criteria remain unverified; order completion does not establish project success.'}
