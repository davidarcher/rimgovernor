"""Bounded squad defense; native targeting and injury supervision retain authority."""
from .combat_health import combat_health_hold
from .strategic_state import fingerprint


async def squad_defense(rt, goal, people):
    from .colony_skills import SkillBlocked, native
    hold = combat_health_hold({'pawns':people})
    if hold:
        raise SkillBlocked(hold)
    threats = rt.batch.native.get('status_after', {}).get('threats', {})
    predators = {p['thingId'] for p in threats.get('huntingPredators', [])
                 if p.get('preyIsOurs') is True and p.get('predatorIsOurs') is False}
    census = await rt.game.query('home/list_pawns', includeDead=False, animals=True, equipment=True)
    enemies = sorted((p for p in census.get('pawns', []) if
        (p.get('hostile') is True or p.get('thingId') in predators) and not p.get('dead') and not p.get('downed')),
        key=lambda p:(p.get('nearestColonistDistance',float('inf')),p['thingId']))
    goal.evidence['threat_assessment'] = enemies
    if len(enemies)==1 and enemies[0].get('humanlike') is True and not ((enemies[0].get('equipment') or {}).get('primary') or {}).get('ranged'):
        return await single_raider_defense(rt, goal, people)
    if not 1 <= len(enemies) <= 4:
        raise SkillBlocked('Defense supports one to four observed opponents; danger hold retained')
    for enemy in enemies:
        if enemy.get('animal') is True:
            if (not 0 < (enemy.get('animals') or {}).get('bodySize', 0) <= 4
                    or enemy.get('mentalState') not in ('Manhunter','ManhunterPermanent') and enemy['thingId'] not in predators):
                raise SkillBlocked('Animal threat is outside the observed squad-defense bound')
        elif enemy.get('humanlike') is not True or not isinstance(enemy.get('equipment'),dict):
            raise SkillBlocked('Opponent capabilities are unavailable or outside squad defense')
    managed = rt.current_plan.control.get('combat', {}).get('pawns', [])
    defenders = [p for p in people if p.get('dead') is False and p.get('downed') is False
        and not p.get('mentalState') and p['thingId'] not in rt.current_plan.control.get('player_draft_overrides', {})
        and (p.get('drafted') is False or p['thingId'] in managed and rt.draft_owners.get(p['thingId']) == rt.context_token)
        and (p.get('bio') or {}).get('incapableOfRead') is True
        and 'Violent' not in p['bio'].get('incapableOfTags', [])]
    defenders.sort(key=lambda p:(-max((s.get('level') or 0 for s in p['bio'].get('skills', [])
                                     if s['name'] in ('Melee','Shooting')),default=0),p['thingId']))
    required = max(2, len(enemies)*2, max(int((p.get('animals') or {}).get('bodySize',1)+.999) for p in enemies))
    if len(defenders) < required:
        raise SkillBlocked(f'Squad defense needs {required} available capable colonists')
    targets = [p['thingId'] for p in enemies]
    goal.evidence['combat_target'] = targets[0]
    goal.evidence['combat_targets'] = targets
    distant = all(p.get('nearestColonistDistance',1000) > 40 for p in enemies)
    method = ('muster-' if distant else 'repel-') + (targets[0] if len(targets)==1 else fingerprint(targets)[:16])
    if goal.method_seen(method):
        return None
    selected = defenders[:required]
    if distant:
        return method, [native('home/order',action='draft',pawn=p['thingId'],watch=False) for p in selected]
    actions = []
    for index, pawn in enumerate(selected):
        target = enemies[index % len(enemies)]
        ranged = ((pawn.get('equipment') or {}).get('primary') or {}).get('ranged') is True
        enemy_ranged = ((target.get('equipment') or {}).get('primary') or {}).get('ranged') is True
        if enemy_ranged and not ranged:
            raise SkillBlocked('Armed ranged opponents require ranged defenders; danger hold retained')
        args = dict(action='attack',mode='ranged' if ranged else 'melee',pawn=pawn['thingId'],target=target['thingId'],watch=False)
        preview = await rt.inspect_native('home/order', dict(args,dryRun=True))
        if preview.get('success') is not True:
            raise SkillBlocked('Native squad attack is not currently legal; danger hold retained')
        actions.append(native('home/order',**args))
    return method, actions


async def single_raider_defense(rt, goal, people):
    from .colony_skills import SkillBlocked, native
    from .bridge import BridgeError
    def unused(name): return not goal.method_seen(name)
    from .combat_health import combat_health_hold
    hold = combat_health_hold({'pawns': people})
    if hold:
        raise SkillBlocked(hold)
    status=rt.batch.native.get('status_after',{}).get('threats',{})
    predators={p['thingId'] for p in status.get('huntingPredators',[]) if p.get('preyIsOurs') is True
        and p.get('predatorIsOurs') is False}
    threats = await rt.game.query('home/list_pawns', includeDead=False, animals=True, equipment=True)
    enemies = [p for p in threats.get('pawns',[]) if (p.get('hostile') is True or p['thingId'] in predators)
        and not p.get('dead') and not p.get('downed')]
    goal.evidence['threat_assessment']=[{k:p.get(k) for k in ('thingId','animal','predator','mentalState','animals','nearestColonistDistance')} for p in enemies]
    if len(enemies)!=1:
        raise SkillBlocked('Threat exceeds the bounded single-enemy defense method; danger hold retained')
    enemy=enemies[0]
    gear=enemy.get('equipment') or {}
    tribal=(enemy.get('humanlike') is True and enemy.get('hostile') is True
        and enemy.get('mechanoid') is False
        and ((gear.get('armed') is False and gear.get('primary') is None)
            or (gear.get('armed') is True and (gear.get('primary') or {}).get('melee') is True
                and (gear.get('primary') or {}).get('ranged') is False)))
    animal=(enemy.get('animal') is True
        and 0 < (enemy.get('animals') or {}).get('bodySize',0) <= 1
        and (enemy.get('mentalState') in ('Manhunter','ManhunterPermanent') or enemy['thingId'] in predators))
    if not animal and not tribal:
        raise SkillBlocked('Threat exceeds the bounded single-animal or melee-raider defense method; danger hold retained')
    target=enemies[0]['thingId']
    goal.evidence['combat_target']=target
    distant=enemies[0].get('nearestColonistDistance',1000)>40
    method=('muster-' if distant else 'repel-')+target
    if not unused(method): return None
    managed=rt.current_plan.control.get('combat',{}).get('pawns',[])
    defenders=[p for p in people if not p.get('dead') and not p.get('downed')
        and p['thingId'] not in rt.current_plan.control.get('player_draft_overrides',{})
        and (not p.get('drafted') or (p['thingId'] in managed and rt.draft_owners.get(p['thingId'])==rt.context_token))
        and not p.get('mentalState') and (p.get('bio') or {}).get('incapableOfRead') is True
        and 'Violent' not in p['bio'].get('incapableOfTags',[])]
    defenders.sort(key=lambda p:(-next((v.get('level',0) or 0 for v in p['bio'].get('skills',[])
        if v['name']=='Melee'),0),p['thingId']))
    count=3 if tribal else 2
    if tribal:
        defenders=[p for p in defenders if isinstance((p.get('health') or {}).get('summaryPct'),(int,float))
            and p['health']['summaryPct']>=.85 and p['health'].get('needsTend') is False]
        defenders.sort(key=lambda p:((p.get('equipment') or {}).get('armed') is not True,
            -next((v.get('level',0) or 0 for v in p['bio'].get('skills',[])
                if v['name']==('Shooting' if ((p.get('equipment') or {}).get('primary') or {}).get('ranged') else 'Melee')),0),p['thingId']))
    if len(defenders)<count: raise SkillBlocked(f'Bounded defense requires {count} available capable healthy colonists')
    if tribal:
        unarmed=next((p for p in defenders[:count] if (p.get('equipment') or {}).get('armed') is not True),None)
        if unarmed:
            if not distant or (unarmed.get('equipment') or {}).get('armed') is not False:
                raise SkillBlocked('Melee-raider defense requires three observed equipped defenders')
            pos=unarmed.get('position') or {}
            if type(pos.get('x')) is not int or type(pos.get('z')) is not int:
                raise SkillBlocked('Unarmed defender position is unavailable')
            stock=await rt.game.query('home/list_things',category='weapons',ownership='ours',includeHeld=False,
                x=pos['x'],z=pos['z'],radius=12,maxPositionsPerDef=8)
            candidates=[(row,item) for row in stock.get('things',[]) if row.get('weapon')
                for item in row.get('positions',[]) if item.get('spawned') is True and item.get('thingId')]
            candidates.sort(key=lambda pair:(pair[0]['weapon'].get('ranged') is not True,pair[1]['thingId']))
            for row,item in candidates[:8]:
                arm='equip-'+unarmed['thingId']+'-'+item['thingId']
                if not unused(arm):continue
                action=native('home/order',action='equip',pawn=unarmed['thingId'],target=item['thingId'],watch=False)
                try:preview=await rt.inspect_native('home/order',dict(action['arguments'],dryRun=True))
                except BridgeError:continue
                if preview.get('success') is True:
                    return arm,[dict(action,completion='pawn_equipped')]
            raise SkillBlocked('No nearby native-approved weapon for the unarmed defender; danger hold retained')
    if distant:
        return method, [native('home/order',action='draft',pawn=p['thingId'],watch=False) for p in defenders[:count]]
    actions=[native('home/order',action='attack',mode='auto' if tribal else 'melee',pawn=p['thingId'],target=target,watch=False,
        requireStandingTarget=True,**({'requireHostile':True} if tribal else {})) for p in defenders[:count]]
    if tribal:
        # Native auto mode uses the equipped weapon. A rifle carrier must
        # not be sent to club a raider merely because melee skill is high.
        previews=[]
        attacks=0
        for index,action in enumerate(actions):
            try:
                preview=await rt.inspect_native('home/order',dict(action['arguments'],dryRun=True))
            except BridgeError as error:
                if 'Verb.CanHitTarget is false' not in error.detail:
                    raise
                # An obstructed shooter keeps ordinary drafted defensive
                # fire. Do not delay other defenders' legal attacks.
                previews.append({'pawn':action['arguments']['pawn'],'refusal':str(error)})
                pawn=action['arguments']['pawn']
                actions[index]=(None if pawn in managed else
                    native('home/order',action='draft',pawn=pawn,watch=False))
                continue
            previews.append(preview)
            if preview.get('success') is not True:
                goal.evidence['firing_solution_refusal']=preview
                return None
            attacks+=1
        goal.evidence['firing_solutions']=previews
        if not attacks:return None
    return method,[action for action in actions if action is not None]
