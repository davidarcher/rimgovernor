"""Python reference for native work proposals and exact mode-aware readback."""
from rimgovernor.colony_policy import work_assignment


def work_reference(pawns, minimum_construction=0, overrides=()):
    preferences = {}
    for value in overrides:
        preferences.setdefault(value['pawn'], {})[value['work']] = value['priority']
    assignments, capacity = work_assignment(pawns, minimum_skills={'Construction': minimum_construction}, overrides=preferences)
    by_id = {p['thingId']: p for p in pawns}
    matches = capacity and all(all(any(w['name'] == name and
        (w.get('priorityStored') == priority if by_id[pawn]['work'].get('manualPriorities')
         else (w.get('priority', 0) > 0) == (priority > 0))
        for w in by_id[pawn]['work']['types']) for name, priority in work.items())
        for pawn, work in assignments.items())
    return {'assignments': assignments, 'capacity': capacity, 'matches': matches,
            'minimum_construction': minimum_construction, 'overrides': list(overrides)}
