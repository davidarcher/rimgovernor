"""Reconsider refused previews only after fresh native prerequisites change."""
from .bridge import BridgeError
from .strategic_state import fingerprint


def refused_preview(error):
    if not isinstance(error, BridgeError) or error.tool != 'home/order':
        return None
    payload = error.result.structuredContent or {}
    if (payload.get('dryRun') is True and payload.get('applied') is False
            and payload.get('success') is False
            and payload.get('errorKind') in ('job_refused', 'work_disabled', 'no_storage',
                'not_reachable', 'target_not_found', 'target_dead', 'pawn_downed', 'mental_state')):
        return payload
    return None


def prerequisites(facts, people, control):
    upkeep = {key: {'known': value.get('known'), 'unsafe': value.get('unsafe'),
        'targets': [{field: row.get(field) for field in
            ('id', 'forbidden', 'burning', 'home', 'inStorage', 'roofed', 'safeWorkers')}
            for row in value.get('targets') or []]} for key, value in control.get('upkeep', {}).items()}
    return fingerprint({'people': [{k: p.get(k) for k in
        ('thingId', 'job', 'jobTargetA', 'jobTargetB', 'carrying', 'drafted', 'downed',
         'dead', 'mentalState', 'orderGeneration', 'health', 'work', 'equipment')}
        for p in people], 'hostiles': facts.get('hostiles'),
        'resources': facts.get('resources'), 'upkeep': upkeep,
        'draft_overrides': control.get('player_draft_overrides'),
        'work_overrides': control.get('work_overrides')})
