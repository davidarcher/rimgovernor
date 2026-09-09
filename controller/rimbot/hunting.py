"""Candidate screening from current native wildlife observations, not path safety."""


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
