"""Furnish a completed player shelter using observed rooms and native footprints."""


def completed_shelters(plan):
    for identity,goal in sorted(plan.colony_goals.items()):
        request=goal.evidence.get('request',{})
        if (not goal.cancelled and goal.status=='complete' and request.get('kind')=='BuildRoom'
                and request.get('purpose','shelter')=='shelter'):
            yield identity,request['room']


async def sleeping_handoff(rt, facts, identity, shell):
    from .colony_skills import SkillBlocked
    bounds=shell['bounds'];x,z=bounds['x'],bounds['z']
    width,height=bounds['width'],bounds['height']
    interior={(a,b) for a in range(x+1,x+width-1) for b in range(z+1,z+height-1)}
    census=await rt.game.query('home/list_rooms',cells=True,x=x+1,z=z+1)
    if census.get('success') is not True:
        raise SkillBlocked('Player shelter room observations are unavailable')
    rooms=[room for room in census.get('rooms',[]) if room.get('cellsComplete') is True
           and {(p['x'],p['z']) for p in room.get('cells',[])}==interior]
    if len(rooms)!=1 or rooms[0].get('properRoom') is not True:
        raise SkillBlocked('Completed player shelter no longer matches an enclosed native room; inspect its shell')
    room=rooms[0]
    if type(room.get('openRoofCount')) is not int:
        raise SkillBlocked('Player shelter roof coverage is unknown')
    if room['openRoofCount']:
        rt.current_plan.control['simulation_needed']=True
        return None
    if room.get('psychologicallyOutdoors') is not False:
        raise SkillBlocked('Player shelter does not have verified indoor conditions')
    required=max(0,facts['colonists']-facts.get('indoorSleepingCapacity',0))
    if not required:return None
    # Keep a continuous central aisle from the requested entrance through the room.
    vertical=shell['entrance'] in ('north','south')
    aisle={(a,b) for a,b in interior if a==x+width//2} if vertical else {
        (a,b) for a,b in interior if b==z+height//2}
    reserved=set(aisle);placements=[];attempts=0
    for a,b in sorted(interior,key=lambda p:(p[1],p[0])):
        if (a,b) in reserved:continue
        for rotation in ('north','east'):
            if attempts>=128:break
            attempts+=1
            preview=await rt.inspect_native('home/place_building',{
                'defName':'SleepingSpot','x':a,'z':b,'rotation':rotation,'dryRun':True})
            rows=[r for r in preview.get('rotations',[]) if r.get('accepted') is True and not r.get('blockingThings')]
            if preview.get('canPlace') is not True or len(rows)!=1:continue
            footprint={(p['x'],p['z']) for p in rows[0].get('occupiedCells',[])}
            if not footprint or not footprint<=interior or footprint&reserved:continue
            placements.append({'def_name':'SleepingSpot','x':a,'z':b,'rotation':rotation})
            reserved|=footprint
            break
        if len(placements)==required:break
    if len(placements)<required:
        raise SkillBlocked('Player shelter has insufficient verified sleeping space; expand or refine this room')
    rt.current_plan.control['adopted_shelter']={'intent':identity,'bounds':bounds,'room_id':room['id']}
    return 'player-sleep-'+str(facts['colonists']),[{'kind':'place_buildings','placements':placements}]
