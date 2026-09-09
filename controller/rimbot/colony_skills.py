"""Domain methods compile into the existing plan actions; only Hands writes."""
from math import ceil
from .colony_plan import PlanStep, RoomShell, Dependency
from .hands import room_placements
from .colony_policy import starter_layouts, work_assignment
from .strategic_state import fingerprint
from .hunting import screen_prey


class SkillBlocked(ValueError):
    pass


def native(tool, **arguments):
    return {'kind': 'native_operation', 'tool': tool, 'arguments': dict(arguments, dryRun=False)}


class ColonySkills:
    def __init__(self, rt):
        self.rt = rt

    async def layout(self, facts):
        control = self.rt.current_plan.control
        if control.get('layout'):
            return control['layout']
        candidates = starter_layouts(facts)
        if not candidates:
            self.rt.note('template_unavailable', 'No starter layout satisfies observed terrain and crop capacity',
                         definitions=facts.get('definitions'), cells=facts.get('cells'), colonists=facts.get('colonists'))
        for layout in candidates[:self.rt.controller.policy.max_method_attempts]:
            shell = self.shell(layout)
            accepted = True
            for p in room_placements(RoomShell.model_validate(shell)):
                preview = await self.rt.inspect_native('home/place_building', dict(defName=p.def_name,
                    x=p.x, z=p.z, rotation=p.rotation, stuff='WoodLog', dryRun=True))
                if preview.get('canPlace') is not True:
                    accepted = False
                    break
            if accepted:
                control['layout'] = layout
                self.rt.note('template_selected', 'Starter layout fits observed terrain and native shell previews', layout=layout)
                self.rt.persist()
                return layout
            self.rt.note('fallback_selected', 'Starter shell refused; checking next ranked site', preview=preview)
        raise SkillBlocked('No legal starter template in bounded nearby search')

    @staticmethod
    def shell(layout):
        return {'kind': 'build_room_shell', 'bounds': layout['room'], 'wall_def': 'Wall',
                'door_def': 'Door', 'materials': ['WoodLog'], 'entrance': 'south'}

    async def designator(self, class_name):
        catalog = await self.rt.inspect_native('rimworld/list_architect_designators', {'categoryId': 'Orders'})
        rows = [r for r in catalog.get('designators', []) if r.get('className') == 'RimWorld.'+class_name]
        if len(rows) != 1:
            raise SkillBlocked('Native designator unavailable: '+class_name)
        return rows[0]['id']

    async def compile(self, goal_id, facts, people):
        """Return a named method and bounded actions, or wait for its postcondition."""
        rt = self.rt
        goal = rt.current_plan.colony_goals[goal_id]
        methods = goal.evidence.setdefault('methods', {})
        def unused(name): return name not in methods
        if goal_id == 'ConfirmColonyNames':
            naming=facts['colonyNaming']
            method='names-'+str(naming['windowId'])
            if unused(method): return method, [native('home/confirm_colony_names',**naming)]
            return None
        if goal_id == 'RestoreWorkers':
            if facts.get('cleanupPawns') and unused('release'):
                return 'release', [{'kind':'stand_down','pawn_ids':facts['cleanupPawns']}]
            return None
        if goal_id == 'ActiveCombat':
            status=rt.batch.native.get('status_after',{}).get('threats',{})
            predators={p['thingId'] for p in status.get('huntingPredators',[]) if p.get('preyIsOurs') is True
                and p.get('predatorIsOurs') is False}
            threats = await rt.game.query('home/list_pawns', includeDead=False, animals=True)
            enemies = [p for p in threats.get('pawns',[]) if (p.get('hostile') is True or p['thingId'] in predators)
                and not p.get('dead') and not p.get('downed')]
            goal.evidence['threat_assessment']=[{k:p.get(k) for k in ('thingId','animal','predator','mentalState','animals','nearestColonistDistance')} for p in enemies]
            if (len(enemies)!=1 or enemies[0].get('animal') is not True
                    or not 0 < (enemies[0].get('animals') or {}).get('bodySize',0) <= 1
                    or (enemies[0].get('mentalState') not in ('Manhunter','ManhunterPermanent')
                        and enemies[0]['thingId'] not in predators)):
                raise SkillBlocked('Threat exceeds the bounded single-animal defense method; danger hold retained')
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
            if len(defenders)<2: raise SkillBlocked('Small-animal defense requires two available capable colonists')
            if distant:
                return method, [native('home/order',action='draft',pawn=p['thingId'],watch=False) for p in defenders[:2]]
            return method, [native('home/order',action='attack',mode='melee',pawn=p['thingId'],target=target,watch=False)
                for p in defenders[:2]]
        if goal_id == 'CriticalMedical':
            if facts.get('medicalKnown') is not True:
                raise SkillBlocked('Native medical state is unavailable; treatment and stability cannot be verified')
            assignments, _ = work_assignment(people)
            doctors = sorted(p for p, w in assignments.items() if w.get('Doctor') == 1)
            if not doctors: raise SkillBlocked('No available doctor')
            for patient in facts['criticalPatients']:
                method = 'tend-'+patient
                if unused(method):
                    return method, [dict(native('home/order', action='tend', pawn=doctors[0], target=patient),
                                         completion='patient_tended')]
            return None
        if goal_id == 'AllowStartingSupplies':
            method = 'allow-'+fingerprint(facts['forbiddenSupplies'][:8])[:8]
            if unused(method):
                designator = await self.designator('Designator_Unforbid')
                return method, [native('rimworld/apply_architect_designator', designatorId=designator,
                    x=p['x'], z=p['z'], keepSelected=False) for p in facts['forbiddenSupplies'][:8]]
            return None
        if goal_id == 'EnsureWorkAssignments':
            assignments, covered = work_assignment(people)
            for pawn, values in rt.current_plan.control.get('work_overrides', {}).items():
                if pawn in assignments: assignments[pawn].update(values)
            if not covered: raise SkillBlocked('Cannot cover doctor, cook, construction and growing with capable available pawns')
            method='assign-'+fingerprint(assignments)[:8]
            if unused(method):
                actions = []
                for pawn, values in assignments.items():
                    observed = next(p for p in people if p['thingId'] == pawn)['work']
                    types = {w['name']:w for w in observed['types']}
                    changed = {name:value for name,value in values.items() if
                        (types[name].get('priorityStored') != value if observed.get('manualPriorities') else
                         (types[name].get('priority',0)>0) != (value>0))}
                    if changed:
                        actions.append(native('home/pawn_config', pawn=pawn, work=','.join(f'{work}={priority}'
                            for work,priority in sorted(changed.items())), watch=False))
                if actions: return method, actions[:8]
                return None
            return None
        if goal_id in ('MaintainWood', 'EnsureFoodSupply'):
            food = goal_id == 'EnsureFoodSupply'
            resource = 'food' if food else 'tree'
            targets = [p for p in facts.get('acquisition', []) if p[resource] and not p['designated']]
            method = 'acquire-'+fingerprint([p['id'] for p in targets[:8]])[:8]
            outstanding = sum(p.get('yield', 0) for p in facts.get('acquisition', []) if p[resource] and p['designated'])
            needed = max(0, rt.controller.policy.wood_target-facts.get('resources', {}).get('WoodLog', 0)-outstanding)
            food_pending = any(p['food'] and p['designated'] for p in facts.get('acquisition', []))
            if targets and unused(method) and ((food and not food_pending) or (not food and needed > 0)):
                designator = await self.designator('Designator_PlantsHarvest' if food else 'Designator_PlantsCut')
                selected = []
                amount = 0
                for plant in targets:
                    if len(selected) >= 8 or (not food and amount >= needed): break
                    selected.append(native('rimworld/apply_architect_designator', designatorId=designator,
                        x=plant['x'], z=plant['z'], keepSelected=False))
                    amount += plant.get('yield', 0)
                if selected: return method, selected
            if not food:
                if not targets and outstanding == 0: raise SkillBlocked('No safe mature trees in acquisition radius')
                return None
            if unused('rice') and not any(f.get('edible') and f.get('usableCells', 0) >= facts['colonists']*10 for f in facts.get('farms', [])):
                layout = await self.layout(facts)
                return 'rice', [{'kind': 'create_zone', 'zone_type': 'growing', 'label': 'RimBot rice',
                                 'crop': 'Plant_Rice', 'patches': layout.get('farms', [layout['farm']])}]
            if facts.get('foodRunwayDays', 0) < rt.controller.policy.food_min_days and facts.get('armed', 0):
                butcher = facts.get('butchering', [])
                if not butcher and unused('butcher-spot'):
                    layout = await self.layout(facts)
                    return 'butcher-spot', [{'kind':'place_buildings','placements':[{'def_name':'ButcherSpot',
                        'x':layout['room']['x']+1,'z':layout['room']['z']+6}]}]
                if butcher and unused('butcher-bill') and not any(b.get('recipe')=='ButcherCorpseFlesh'
                        and b.get('suspended') is False for b in butcher[0].get('bills', [])):
                    return 'butcher-bill', [native('home/bills',action='add',bench=butcher[0]['id'],recipe='ButcherCorpseFlesh',
                        repeatMode='Forever',ingredientSearchRadius=999,watch=False)]
                if butcher:
                    wildlife = await rt.game.query('home/list_pawns', wildOnly=True, animalsOnly=True, animals=True)
                    if sum((p.get('animals') or {}).get('designations', {}).get('hunt') is True
                           for p in wildlife.get('pawns', [])) >= 2: return None
                    home=rt.current_plan.control.get('layout',{}).get('room')
                    anchor={'x':home['x']+home['width']//2,'z':home['z']+home['height']//2} if home else facts['center']
                    prey,evidence=screen_prey(wildlife.get('pawns',[]),anchor)
                    goal.evidence['hunting_screen']=evidence
                    target = next((p for p in prey if unused('hunt-'+p['thingId'])),None)
                    if target:
                        designator = await self.designator('Designator_Hunt')
                        return 'hunt-'+target['thingId'], [native('rimworld/apply_architect_designator',
                            designatorId=designator,x=target['position']['x'],z=target['position']['z'],keepSelected=False)]
            return None
        if goal_id == 'EnsureInitialShelter':
            layout = await self.layout(facts)
            if unused('shell') and facts.get('indoorSleepingCapacity', 0) < facts['colonists']:
                return 'shell', [self.shell(layout)]
            if unused('sleeping'):
                room = layout['room']
                placements = [{'def_name': 'SleepingSpot', 'x': room['x']+x, 'z': room['z']+z}
                              for z in (1, 3) for x in (1, 3, 5, 7)]
                if facts['colonists'] > len(placements): raise SkillBlocked('Starter template supports at most eight colonists')
                return 'sleeping', [{'kind': 'place_buildings', 'placements': placements[:facts['colonists']]}]
            return None
        if goal_id == 'EnsureFoodStorage':
            if unused('storage'):
                layout = await self.layout(facts)
                return 'storage', [{'kind': 'create_zone', 'zone_type': 'stockpile', 'label': 'RimBot food',
                    'patches': [layout['storage']], 'preset': 'food', 'priority': 'Important'}]
            return None
        if goal_id == 'EnsureCooking':
            if not facts.get('cooking') and unused('campfire'):
                layout = await self.layout(facts)
                return 'campfire', [{'kind': 'place_buildings', 'placements': [{'def_name': 'Campfire',
                    'x': layout['room']['x']+6, 'z': layout['room']['z']+6}]}]
            for bench in facts.get('cooking', []):
                if bench.get('recipes') and unused('bill'):
                    recipe = 'CookMealSimple' if 'CookMealSimple' in bench['recipes'] else sorted(bench['recipes'])[0]
                    return 'bill', [native('home/bills', action='add', bench=bench['id'].removeprefix('Thing_'), recipe=recipe,
                        repeatMode='TargetCount', targetCount=facts['colonists']*3,
                        unpauseWhenYouHave=facts['colonists'], pauseWhenSatisfied='on', ingredientSearchRadius=40, watch=False)]
            return None
        if goal_id == 'EnsureTemperatureSafety':
            if facts.get('indoorSleepingCapacity', 0) < facts['colonists']: return None
            if unused('thermal'):
                layout = await self.layout(facts)
                cold = facts.get('sleepingTemperatureMin', 20) < rt.controller.policy.temperature_enter_low
                if cold and facts.get('cooking'): return None  # A fueled campfire already heats the shared starter room.
                definition = 'Campfire' if cold else 'PassiveCooler'
                return 'thermal', [{'kind': 'place_buildings', 'placements': [{'def_name': definition,
                    'x': layout['room']['x']+2, 'z': layout['room']['z']+6}]}]
            return None
        if goal_id == 'EnsureBasicPower':
            raise SkillBlocked('Electrical load requires power; select an available generation plan')
        if goal_id == 'EnsureBasicDefense':
            if unused('equip'):
                weapons = await rt.game.query('home/list_things', category='weapons', ownership='ours',
                    includeHeld=False, x=facts['center']['x'], z=facts['center']['z'], radius=25, maxPositionsPerDef=12)
                targets = sorted(p['thingId'] for w in weapons.get('things', []) if w.get('oursUnforbidden', 0)>0
                                 for p in w.get('positions', []))
                candidates = [p for p in people if not p.get('dead') and not p.get('downed')
                    and not p.get('drafted') and (p.get('equipment') or {}).get('armed') is False
                    and (p.get('bio') or {}).get('incapableOfRead') is True
                    and 'Violent' not in (p.get('bio') or {}).get('incapableOfTags', [])]
                candidates.sort(key=lambda p:(-next((s.get('level',0) or 0 for s in p['bio'].get('skills',[])
                    if s['name']=='Shooting'),0),p['thingId']))
                required = max(0, min(2, facts['colonists'])-facts['armed'])
                actions = [dict(native('home/order', action='equip', pawn=pawn['thingId'], target=weapon, watch=False),
                                completion='pawn_equipped') for pawn, weapon in zip(candidates[:required], targets)]
                if not actions: raise SkillBlocked('No available combatant and accessible weapon pair')
                return 'equip', actions
            return None
        raise SkillBlocked('No encoded method for '+goal_id)

    def steps(self, goal_id, method, actions, facts):
        goal = self.rt.current_plan.colony_goals[goal_id]
        result, slots = [], {}
        for index, action in enumerate(actions):
            identity = f'{goal_id}-{goal.attempts}-{method}-{index}'[:64]
            step = PlanStep(id=identity, title=f'{goal_id}: {method}', goal_id=goal_id, source=goal.source,
                priority=max(75 if goal.source=='PLAYER' else 0,100-goal.priority_class*20), action=action,
                completion_criteria='Native effect observed; colony goal separately verifies functional postconditions')
            if goal_id=='EnsureFoodSupply' and method.startswith('hunt-'):
                home=self.rt.current_plan.control.get('layout',{}).get('room')
                anchor={'x':home['x']+home['width']//2,'z':home['z']+home['height']//2} if home else facts['center']
                goal.evidence.setdefault('hunting_targets',{})[identity]={'prey':method.removeprefix('hunt-'),
                    'anchor':dict(anchor),'signature':step.signature()}
            if index:
                step.after = [Dependency(step=result[-1].id, when='complete')]
            costs = {}
            placements = room_placements(step.action) if action['kind'] == 'build_room_shell' else (
                step.action.placements if action['kind'] == 'place_buildings' else [])
            for slot, placement in enumerate(placements):
                definition = facts.get('definitions', {}).get(placement.def_name)
                if definition is None or definition.get('available') is not True:
                    raise SkillBlocked('No available observed construction definition: '+placement.def_name)
                costs[str(slot)] = definition['costs']
            slots[identity] = costs
            result.append(step)
        return result, slots
