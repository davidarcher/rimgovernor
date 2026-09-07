"""Explicit benchmark criteria, not a policy for every colony."""
from .food_access import runway


def assess(observation, buildings, target_defs, population):
    rooms=(observation.get('rooms') or {}).get('rooms')
    sheltered=set()
    if rooms is not None:
        for room in rooms:
            if (not room['touches_map_edge'] and not room['is_doorway'] and not room['is_prison_cell']
                    and room['open_roof_count']==0 and room.get('pawns_reaching_visible_cell')):
                sheltered.update(room.get('contained_beds_ids') or [])
    sleeping=sum(b['state']=='built' and b['def_name'] in target_defs and b['thing_id'] in sheltered for b in buildings)
    zones=(observation.get('zones') or {}).get('zones')
    stockpiles=sum(z.get('type')=='Zone_Stockpile' and z.get('cells_count',0)>0 for z in (zones or []))
    summary=((observation.get('resources') or {}).get('critical_resources') or {}).get('food_summary') or {}
    food=runway(summary.get('access')).get('food_runway_days')
    checks={'sheltered_sleeping_objects':None if rooms is None else sleeping,
            'stockpiles':None if zones is None else stockpiles,'food_coverage_days':food,
            'colonists':observation.get('game',{}).get('colonist_count')}
    return {'passed':checks['colonists']==population and sleeping>=population and stockpiles>0 and food is not None and food>=.5,
            'checks':checks,'scope':'Starter checkpoint: one completed target sleeping object per colonist in enclosed roofed non-prison rooms with reachable anchors, a stockpile, and at least half a day of accessible food. Not proof of long-term sustainability, bed ownership or access to every furniture cell.'}
