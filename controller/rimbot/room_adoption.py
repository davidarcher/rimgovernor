"""Explicitly select an existing native room without rewriting its construction history."""
from .colony_plan import ColonyGoal,RoomBounds


async def adopt(rt, request, *, token, revision):
    from .shelter_handoff import verified_room,requested_shell
    from .colony_skills import SkillBlocked
    plan=rt.current_plan
    identity='intent-'+request.intent_id
    prior=plan.colony_goals.get(identity)
    if prior and prior.evidence.get('request',{}).get('kind')!='AdoptRoom':
        raise ValueError('Use a new room-adoption intent ID; existing construction history must remain intact')
    shell=requested_shell(request.model_dump())
    try:
        observed=await verified_room(rt,shell,request_simulation=False)
    except SkillBlocked as error:
        raise ValueError(str(error)) from error
    if observed is None:
        raise ValueError('Room adoption requires completed native roof coverage')
    room,interior=observed
    await verify_entrance(rt,shell)
    await rt.ensure_context(token)
    if rt.chat_revision!=revision or rt.current_plan is not plan:
        raise ValueError('Player direction changed; room adoption discarded')
    goal=plan.colony_goals.setdefault(identity,ColonyGoal(source='PLAYER',priority_class=2))
    goal.source,goal.cancelled,goal.status,goal.reason='PLAYER',False,'complete',''
    goal.target={'satisfies':'EnsureInitialShelter','intent_id':request.intent_id}
    goal.evidence['request']=request.model_dump()
    goal.evidence['adoption']={'room_id':room['id'],'cells':len(interior),
        'native_role':room.get('role'),'native_role_label':room.get('roleLabel'),
        **{key:rt.identity[key] for key in ('colonyId','mapId','loadToken')}}
    plan.control['preferred_shelter']=identity
    plan.control.setdefault('suppressed_goals',{}).pop('EnsureInitialShelter',None)
    return {'goal':identity,'native_room':room['id'],'orders':'Existing construction preserved; no new construction issued'}


async def verify_entrance(rt,shell):
    bounds=RoomBounds.model_validate(shell['bounds'])
    # Verify a real entrance on the requested perimeter side, not a guessed aisle.
    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=False,
        x=bounds.x+bounds.width//2,z=bounds.z+bounds.height//2,
        radius=max(bounds.width,bounds.height))
    if buildings.get('success') is not True or buildings.get('skipped',{}).get('byMaxDetailed'):
        raise ValueError('Room entrance observations are incomplete')
    entrance=(bounds.x+bounds.width//2, bounds.z if shell['entrance']=='south' else bounds.z+bounds.height-1) if shell['entrance'] in ('north','south') else (
        bounds.x if shell['entrance']=='west' else bounds.x+bounds.width-1,bounds.z+bounds.height//2)
    if shell.get('entrance_cell'):
        entrance=(shell['entrance_cell']['x'],shell['entrance_cell']['z'])
    doorway=await rt.game.query('home/list_rooms',x=entrance[0],z=entrance[1],includeOutdoors=True)
    doors={door.get('doorDef') for door in doorway.get('rooms',[]) if door.get('isDoorway') is True and door.get('doorDef')}
    # The native doorway classification, rather than a name fragment, establishes
    # the entrance. A blueprint at that cell is not a usable door.
    if doorway.get('success') is not True or not any(b.get('defName') in doors and (b.get('position',{}).get('x'),b.get('position',{}).get('z'))==entrance
               and b.get('isBlueprint') is False and b.get('isFrame') is False for b in buildings.get('buildings',[])):
        raise ValueError('The requested central entrance is not an observed completed native boundary door')


def validate_adoption_context(rt, goal):
    if goal.evidence.get('request',{}).get('kind')!='AdoptRoom':return
    from .colony_skills import SkillBlocked
    evidence=goal.evidence.get('adoption',{})
    if any(evidence.get(key)!=rt.identity.get(key) for key in ('colonyId','mapId','loadToken')):
        raise SkillBlocked('Room adoption belongs to another load; inspect and explicitly adopt the current room again')


async def validate_adoption(rt,goal):
    validate_adoption_context(rt,goal)
    if goal.evidence.get('request',{}).get('kind')!='AdoptRoom':return
    from .colony_skills import SkillBlocked
    from .shelter_handoff import requested_shell
    try:
        await verify_entrance(rt,requested_shell(goal.evidence['request']))
    except ValueError as error:
        raise SkillBlocked(str(error)) from error
