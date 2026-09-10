"""Rank observed patients and available doctors; native previews decide legality."""
from math import isfinite


def treatment_pairs(people, patients, control):
    def urgency(pawn):
        health = pawn.get('health') or {}
        hours = health.get('hoursUntilDeathFromBloodLoss')
        deadline = hours if type(hours) in (int, float) and isfinite(hours) and hours >= 0 else float('inf')
        return deadline, not pawn.get('downed'), pawn['thingId']

    doctors = []
    for pawn in people:
        identity = pawn['thingId']
        if (pawn.get('dead') is not False or pawn.get('downed') is not False
                or pawn.get('drafted') is not False or pawn.get('mentalState')
                or pawn.get('job') == 'TendPatient'
                or identity in control.get('player_draft_overrides', {})
                or control.get('work_overrides', {}).get(identity, {}).get('Doctor') == 0
                or not any(w.get('name') == 'Doctor' and w.get('disabled') is False
                           for w in (pawn.get('work') or {}).get('types', []))):
            continue
        skill = next((s.get('level') for s in (pawn.get('bio') or {}).get('skills', [])
                      if s.get('name') == 'Medicine' and not s.get('disabled')), None)
        if type(skill) not in (int, float) or not isfinite(skill):
            continue
        doctors.append((-skill, identity, pawn))
    ordered = sorted((p for p in people if p['thingId'] in patients
                      and p.get('dead') is False and (p.get('health') or {}).get('needsTend') is True), key=urgency)
    for patient in ordered:
        for _, identity, doctor in sorted(doctors, key=lambda row: row[:2]):
            if identity == patient['thingId'] and (doctor.get('health') or {}).get('selfTend') is not True:
                continue
            yield patient['thingId'], identity
