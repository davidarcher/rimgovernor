"""Small colony-scoped notebook. Notes are advisory, never execution state."""
from copy import deepcopy
import re


def update_memory(notes, operation, id, text=None, evidence=None, *, tick, load_token):
    if not isinstance(id, str) or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,59}', id):
        raise ValueError('Use a descriptive lowercase memory ID, at most 60 characters')
    if operation not in ('read', 'write', 'delete'):
        raise ValueError('Unknown memory operation')
    if operation != 'write' and (text is not None or evidence is not None):
        raise ValueError('Only write accepts text and evidence')
    if operation == 'read':
        if id not in notes:
            raise ValueError('Unknown memory ID')
        return dict(note=deepcopy(notes[id]), from_previous_load=notes[id]['load_token'] != load_token,
                    advisory=True)
    if operation == 'delete':
        return {'id': id, 'deleted': notes.pop(id, None) is not None}
    if not isinstance(text, str) or not 1 <= len(text.strip()) <= 1000:
        raise ValueError('Memory text must contain 1–1000 characters')
    if not isinstance(evidence, str) or not 1 <= len(evidence.strip()) <= 500:
        raise ValueError('Provide 1–500 characters describing the evidence and uncertainty')
    if id not in notes and len(notes) >= 20:
        raise ValueError('Notebook has 20 notes; update or delete an old note first')
    notes[id] = dict(id=id, text=text.strip(), evidence=evidence.strip(), tick=tick, load_token=load_token)
    return {'id': id, 'saved': True, 'advisory': True}
