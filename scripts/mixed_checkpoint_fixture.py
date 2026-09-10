"""Prepare native partial construction plus pending zone/work for paired restart."""
from rimbot.colony_plan import ColonyGoal,CommitSteps
from rimbot.player_commands import apply_command


async def prepare_mixed(rt, *, compact=False):
    facts=await rt.game.query('home/colony_facts',planning=True)
    while facts.get('forbiddenSupplies'):
        goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
        method,actions=await rt.controller.skills.compile('AllowStartingSupplies',facts,[])
        steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Permit native starting stock for mixed checkpoint acceptance',steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
        goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
        facts=await rt.game.query('home/colony_facts',planning=True)
    layout=await rt.controller.skills.layout(facts)
    command={'kind':'BuildRoom','intent_id':'mixed-home','purpose':'shelter',
        'room':rt.controller.skills.shell(layout)}
    if compact:
        from rimbot.colony_plan import RoomShell
        from rimbot.hands import room_placements
        placements=room_placements(RoomShell.model_validate(command['room']))[:2]
        command={'kind':'PlaceBuildings','purpose':'shelter','buildings':{
            'kind':'place_buildings','placements':[p.model_dump(mode='json') for p in placements]}}
    shell=await apply_command(rt,command,token=rt.context_token,revision=rt.chat_revision)
    rt.manual_requests=[]
    rt.manual_execution=(rt.context_token,rt.chat_revision,rt.current_plan.revision)
    try:await rt.hands.advance(rt,max_operations=1,only_ids={shell['step']})
    finally:rt.manual_execution=None
    p=rt.current_plan.progress[shell['step']]
    assert len(p.issued)==1 and p.issued['0']['confirmed']
    assert len(rt.current_plan.control['costs'][shell['step']])>1
    zone=await apply_command(rt,{'kind':'CreateZone','intent_id':'mixed-field','zone':{
        'kind':'create_zone','zone_type':'growing','label':'Mixed checkpoint rice','crop':'Plant_Rice',
        'patches':[layout['farms'][0]]}},token=rt.context_token,revision=rt.chat_revision)
    roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
    pawn=next(p for p in roster['pawns'] if any(w['name']=='Cleaning' and w.get('disabled') is False for w in p['work']['types']))
    work=await apply_command(rt,{'kind':'SetWorkPriority','pawn':pawn['thingId'],'work_type':'Cleaning','priority':0},
        token=rt.context_token,revision=rt.chat_revision)
    rt.manual_requests=[]
    assert rt.current_plan.progress[zone['step']].state=='pending' and rt.current_plan.progress[work['step']].state=='pending'
    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    zones=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    rt.persist()
    return {'shell':shell['step'],'zone':zone['step'],'work':work['step'],
        'buildings':buildings,'zones':zones,'costs':rt.current_plan.control['costs']}


async def verify_mixed(rt,evidence):
    plan=rt.current_plan
    assert plan.progress[evidence['zone']].state=='pending' and plan.progress[evidence['work']].state=='pending'
    assert len(plan.progress[evidence['shell']].issued)==1
    assert plan.control['costs']==evidence['costs']
    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    old={b['thingId'] for b in evidence['buildings']['buildings']}
    assert {b['thingId'] for b in buildings['buildings']}==old
    zones=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000)
    assert zones['zones']==evidence['zones']['zones']
    assert rt.counters['actions']==0 and not rt.manual_requests
    return {'native_building_ids':sorted(old),'pending_preserved':True,'reservations_preserved':True,'no_replay':True}


async def verify_rewind(rt,evidence):
    old_token=rt.context_token;old_revision=rt.chat_revision
    await rt.bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
    await rt.sync_identity()
    assert rt.context_token!=old_token and rt.mode=='manual'
    await rt.projects.reconcile(rt.game)
    rt.reconcile_plan()
    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    prior={b['thingId'] for b in evidence['buildings']['buildings']}
    current={b['thingId'] for b in buildings['buildings']}
    assert prior-current,'Rewind did not remove the later issued shell fixture'
    # Deliberately deliver the saved queued request under its obsolete load authority.
    rt.manual_requests=[(evidence['zone'],old_token,old_revision)]
    before=rt.counters['actions']
    await rt.execute_manual_requests()
    assert rt.counters['actions']==before and not rt.manual_requests
    rejected=False
    try:await rt.native('home/place_building',{'defName':'Wall','stuff':'WoodLog','x':1,'z':1,'dryRun':False},
        expected_token=old_token,expected_revision=old_revision)
    except (ValueError,InterruptedError):rejected=True
    assert rejected and rt.counters['actions']==before
    after=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    assert {b['thingId'] for b in after['buildings']}==current
    return {'new_load':rt.context_token,'obsolete_load':old_token,'removed_future_buildings':sorted(prior-current),
        'stale_queue_discarded':True,'stale_write_refused':True,'mode':rt.mode,
        'plan':rt.current_plan.model_dump()}
