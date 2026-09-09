"""Candidate screening from current native wildlife observations, not path safety."""


class HuntingRefused(ValueError):
    """A known pre-write refusal, never an ambiguous native receipt."""


async def validate_hunt(game,arguments,target):
    if not target: raise HuntingRefused('Hunting target identity is missing; no designation sent')
    wildlife=await game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
    if wildlife.get('success') is False or not isinstance(wildlife.get('pawns'),list):
        raise HuntingRefused('Wildlife observation unavailable; no designation sent')
    prey=next((p for p in wildlife['pawns'] if p.get('thingId')==target['prey']),None)
    if not prey or prey.get('position')!={'x':arguments.get('x'),'z':arguments.get('z')}:
        raise HuntingRefused('Selected prey moved or disappeared; no designation sent')
    candidates,evidence=screen_prey(wildlife['pawns'],target['anchor'])
    if target['prey'] not in evidence['candidates']:
        raise HuntingRefused('Selected prey no longer passes hunting screening; no designation sent')
    if sum((p.get('animals') or {}).get('designations',{}).get('hunt') is True for p in wildlife['pawns'])>=2:
        raise HuntingRefused('Two hunting designations already exist; no designation sent')
    return evidence


def screen_prey(pawns,anchor,*,predator_radius=25,max_distance=50):
    distance=lambda a,b:max(abs(a['x']-b['x']),abs(a['z']-b['z']))
    threats=[p for p in pawns if p.get('dead') is not True and p.get('predator') is not False]
    unknown=any(p.get('predator') is not True or not p.get('position') for p in threats)
    candidates,rejected=[],[]
    for pawn in pawns:
        if not (pawn.get('hostile') is False and pawn.get('predator') is False
                and pawn.get('manhunterOnDamageChance')==0 and pawn.get('dead') is False
                and pawn.get('downed') is False and pawn.get('position')
                and distance(pawn['position'],anchor)<=max_distance
                and (pawn.get('animals') or {}).get('designations',{}).get('hunt') is False):
            continue
        nearby=[p['thingId'] for p in threats if p.get('position')
                and distance(p['position'],pawn['position'])<=predator_radius]
        if unknown or nearby:
            rejected.append({'prey':pawn['thingId'],'reason':'Predator observations unavailable' if unknown else 'Nearby predator',
                             'predators':sorted(nearby)})
        else: candidates.append(pawn)
    # Body size ranks candidates; it never credits food that has not been acquired.
    candidates.sort(key=lambda p:(-((p.get('animals') or {}).get('bodySize') or 0)/
        (1+distance(p['position'],anchor)/25),distance(p['position'],anchor),p['thingId']))
    return candidates,{'predator_radius':predator_radius,'max_distance':max_distance,'rejected':rejected,
                       'candidates':[p['thingId'] for p in candidates]}
