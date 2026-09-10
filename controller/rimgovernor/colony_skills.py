"""Domain methods compile into the existing plan actions; only Hands writes."""
from .colony_policy import required_colony_work
from math import ceil
from .colony_plan import PlanStep, RoomShell, Dependency
from .hands import room_placements
from .colony_policy import starter_layouts, work_assignment, farm_patches
from .strategic_state import fingerprint
from .hunting import screen_prey
from .food_forecast import acquisition_targets
from .bridge import BridgeError
from .supply_batches import supply_rectangles


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
            plan=self.rt.current_plan
            protected={cell for step in plan.spec.steps if step.source=='PLAYER' and isinstance(step.action,RoomShell)
                       for cell in step.action.bounds.cells()}
            protected.update(cell for walkway in plan.spec.reserved_walkways for cell in walkway.cells())
            cached=control['layout']
            farm_cells={(x,z) for p in farm_patches(cached)
                        for x in range(p['x'],p['x']+p['width']) for z in range(p['z'],p['z']+p['height'])}
            if protected&farm_cells:
                if any(s.action.kind=='create_zone' and s.action.zone_type=='growing' and plan.progress[s.id].issued
                       for s in plan.spec.steps):
                    raise SkillBlocked('Issued growing zones conflict with player construction; refine existing zones explicitly')
                filtered=dict(facts,cells=[dict(c,occupied=True) if (c['x'],c['z']) in protected else c for c in facts.get('cells',[])])
                alternatives=starter_layouts(filtered)
                if not alternatives:raise SkillBlocked('No observed fertile field space outside accepted player construction')
                control['layout']=dict(cached,farm=alternatives[0]['farm'],farms=alternatives[0]['farms'])
                self.rt.note('farm_layout_revised','Unissued fields now avoid accepted player construction',farms=control['layout']['farms'])
                self.rt.persist()
            return control['layout']
        candidates = starter_layouts(facts)
        if not candidates:
            self.rt.note('template_unavailable', 'No starter layout satisfies observed terrain and crop capacity',
                         definitions=facts.get('definitions'), cells=facts.get('cells'), colonists=facts.get('colonists'))
        from .construction_preflight import preflight_construction
        token,direction=self.rt.context_token,self.rt.chat_revision
        plan=self.rt.current_plan;comparisons=[];accepted=[]
        for index,layout in enumerate(candidates[:self.rt.controller.policy.max_method_attempts]):
            identity='layout-preview-'+str(index)
            if any(s.id==identity for s in plan.spec.steps):raise SkillBlocked('Layout preview identity is already in use')
            candidate=PlanStep(id=identity,title='Candidate shelter',action=self.shell(layout),
                completion_criteria='Native shelter complete')
            spec=plan.spec.model_copy(deep=True);spec.steps.append(candidate)
            try:
                evidence=await preflight_construction(spec,plan,self.rt.game)
            except ValueError as error:
                comparisons.append(dict(bounds=layout['room'],accepted=False,reason=str(error),
                    evidence=getattr(error,'evidence',{})))
                continue
            await self.rt.ensure_context(token)
            if self.rt.chat_revision!=direction or self.rt.current_plan is not plan:
                raise InterruptedError('Player direction changed during layout comparison')
            costs=evidence['costs'].get(identity,{})
            shortage=sum(max(0,amount-evidence['stock'].get(resource,0)) for resource,amount in costs.items())
            from .spatial import room_entrance
            (x,z),(dx,dz)=room_entrance(candidate.action)
            approaches={(x-dx,z-dz),(x+dx,z+dz)}
            routes=[target['projectedSteps'] for pawn in evidence['access'].get('pawns',[])
                for target in pawn.get('targets',[]) if target.get('nativeReachable') is True
                and (target.get('x'),target.get('z')) in approaches
                and type(target.get('projectedSteps')) is int]
            travel=min(routes) if routes else None
            comparison=dict(bounds=layout['room'],accepted=True,costs=costs,available=evidence['stock'],
                shortage=shortage,travel_steps=travel,farm_cells=sum(p['width']*p['height'] for p in farm_patches(layout)),
                access_ms=evidence['access'].get('elapsedMilliseconds'))
            comparisons.append(comparison)
            accepted.append(((shortage,travel if travel is not None else float('inf'),index),layout,comparison))
        await self.rt.ensure_context(token)
        if self.rt.chat_revision!=direction or self.rt.current_plan is not plan:
            raise InterruptedError('Player direction changed during layout comparison')
        control['layout_comparison']=comparisons
        if accepted:
            _,layout,evidence=min(accepted,key=lambda row:row[0])
            control['layout']=layout
            self.rt.note('template_selected','Shelter selected from bounded native site, supply and route comparisons',
                layout=layout,evidence=evidence)
            self.rt.persist()
            return layout
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

    async def cooking_fallback(self, facts):
        """A replacement cooking service does not require a new house footprint."""
        plan = self.rt.current_plan
        protected = {cell for path in plan.spec.reserved_walkways for cell in path.cells()}
        for step in plan.spec.steps:
            if isinstance(step.action, RoomShell):
                protected.update(step.action.bounds.cells())
        center = facts['center']
        candidates = [c for c in facts.get('cells', [])
                      if c.get('walkable') is True and c.get('supportsLight') is True
                      and c.get('occupied') is False and c.get('zone') is False
                      and (c['x'], c['z']) not in protected
                      and max(abs(c['x']-center['x']), abs(c['z']-center['z'])) <= 6]
        candidates.sort(key=lambda c: (not c.get('roofed', False),
            (c['x']-center['x'])**2 + (c['z']-center['z'])**2, c['x'], c['z']))
        for cell in candidates[:8]:
            preview = await self.rt.inspect_native('home/place_building', dict(
                defName='Campfire', x=cell['x'], z=cell['z'], dryRun=True))
            if preview.get('canPlace') is True:
                return [{'kind': 'place_buildings', 'placements': [
                    {'def_name': 'Campfire', 'x': cell['x'], 'z': cell['z']}]}]
        raise SkillBlocked('No legal nearby cooking fallback in the bounded native search')

    async def compile(self, goal_id, facts, people):
        if goal_id == 'RecoverDisasterServices':
            from .service_recovery import compile_method
            return await compile_method(self.rt, self.rt.current_plan.colony_goals[goal_id], facts, people)
        if goal_id == 'MaintainWaste':
            from .waste_management import compile_method
            return await compile_method(self.rt, self.rt.current_plan.colony_goals[goal_id])
        if goal_id == 'EnsureResearch':
            from .research import method
            return await method(self.rt, facts)
        if goal_id == 'MaintainEquipment':
            from .gear_upkeep import compile_method
            return await compile_method(self.rt, facts)
        if goal_id.startswith('EnsureMood-'):
            from .mood_control import method
            return await method(self.rt, goal_id, facts, people)
        if goal_id == 'EnsureInitialShelter' and facts.get('populationHousingTarget'):
            facts = dict(facts, colonists=max(facts['colonists'], facts['populationHousingTarget']))
        if goal_id == 'EnsureFoodSupply' and facts.get('populationNutritionPerDay'):
            facts = dict(facts, nutritionPerDay=facts['populationNutritionPerDay'],
                         foodRunwayDays=facts['populationFoodRunwayDays'],
                         colonists=max(facts['colonists'], facts.get('populationHousingTarget', 0)))
        if goal_id.startswith('Population-'):
            from .population import compile_method
            return await compile_method(self.rt, goal_id, facts, people)
        if goal_id.startswith('MaintainHerd-'):
            from .husbandry import husbandry_method
            return await husbandry_method(self.rt, goal_id)

        if goal_id == 'MaintainMedicalCare':
            from .medical_management import manage_care
            return await manage_care(self.rt, facts, people)
        from .colony_upkeep import GOALS, upkeep_method
        if goal_id in GOALS:
            return await upkeep_method(self.rt, goal_id, facts, people)
        if goal_id.startswith('MaintainResource-'):
            from .production_policy import resource_method
            return await resource_method(self.rt, goal_id, facts)
        """Return a named method and bounded actions, or wait for its postcondition."""
        rt = self.rt
        if goal_id == 'EnsureBasicPower' and any(c.get('defName') == 'SolarFlare' for c in facts.get('environment', {}).get('conditions', [])):
            rt.current_plan.control['simulation_needed'] = True
            return None
        if goal_id in ('EnsureBasicPower', 'EnsureResearch', 'EnsureComfort', 'EnsureExpansion'):
            from .development import development_method
            return await development_method(self, goal_id, facts, people)
        goal = rt.current_plan.colony_goals[goal_id]
        goal.evidence.setdefault('methods', {})
        def unused(name): return not goal.method_seen(name)
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
            from .combat_method import squad_defense
            return await squad_defense(rt, goal, people)
        if goal_id == 'CriticalMedical':
            if facts.get('medicalKnown') is not True:
                raise SkillBlocked('Native medical state is unavailable; treatment and stability cannot be verified')
            from .medical_triage import treatment_pairs
            if any(p.get('job') == 'TendPatient' for p in people):
                return None
            refusals = []
            for patient, doctor in treatment_pairs(people, facts['criticalPatients'], rt.current_plan.control):
                method = 'tend-'+patient
                completed = goal.archived_methods + sum(
                    step.action.kind == 'native_operation'
                    and step.action.completion == 'patient_tended'
                    and step.action.arguments.get('target') == patient
                    and rt.current_plan.progress[step.id].state == 'complete'
                    for step in rt.current_plan.spec.steps if step.goal_id == goal_id)
                if completed:
                    method += '-'+str(completed)
                if unused(method):
                    args = dict(action='tend', pawn=doctor, target=patient, dryRun=True)
                    from .bridge import BridgeError
                    from .order_refusal import refused_preview
                    try:
                        preview = await rt.inspect_native('home/order', args)
                    except BridgeError as error:
                        preview = refused_preview(error)
                        if preview is None:
                            raise
                    if preview.get('success') is True:
                        goal.evidence['triage'] = {'patient': patient, 'doctor': doctor, 'refusals': refusals}
                        return method, [dict(native('home/order', action='tend', pawn=doctor, target=patient),
                                             completion='patient_tended')]
                    refusals.append({'patient': patient, 'doctor': doctor, 'preview': preview})
                    if len(refusals) >= 8:
                        break
            goal.evidence['triage'] = {'refusals': refusals}
            # Release combat ownership before selecting a doctor on the next read.
            # An active or unknown threat must not lose its defenders to cleanup.
            if facts.get('hostiles') == 0:
                owned = [p['thingId'] for p in people if p.get('drafted') is True
                    and getattr(rt, 'draft_owners', {}).get(p['thingId']) == rt.context_token
                    and p['thingId'] not in rt.current_plan.control.get('player_draft_overrides', {})
                    and p.get('job') != 'TendPatient']
                method = 'release-medical-'+fingerprint(sorted(owned))[:12]
                if owned and unused(method):
                    return method, [{'kind': 'stand_down', 'pawn_ids': sorted(owned)}]
            if any((p.get('health') or {}).get('needsTend') is True
                   for p in people if p['thingId'] in facts['criticalPatients']):
                raise SkillBlocked('No available native-approved doctor/patient pair; medical hold retained')
            return None
        if goal_id == 'AllowStartingSupplies':
            method = 'allow-'+fingerprint(facts['forbiddenSupplies'][:8])[:8]
            if unused(method):
                designator = await self.designator('Designator_Unforbid')
                return method, [native('rimworld/apply_architect_designator', designatorId=designator,
                    **rectangle, keepSelected=False) for rectangle in supply_rectangles(facts['forbiddenSupplies'][:8])]
            return None
        if goal_id == 'EnsureWorkAssignments':
            from .husbandry import required_handler_skill
            assignments, covered = work_assignment(people, required_colony_work(rt.current_plan), rt.current_plan.control.get('work_overrides', {}), required_handler_skill(rt.current_plan))
            for pawn, values in rt.current_plan.control.get('work_overrides', {}).items():
                if pawn in assignments: assignments[pawn].update(values)
            covered = covered and all(any(values.get(work, 0) > 0 for values in assignments.values())
                for work in {'Doctor', 'Cooking', 'Construction', 'Growing', *required_colony_work(rt.current_plan)})
            if not covered: raise SkillBlocked('Required colony or resource work lacks an available capable pawn or is disabled by player work overrides')
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
            batch=actions[:8]
            method='assign-'+fingerprint(batch)[:8]
            if batch and unused(method):return method,batch
            return None
        if goal_id in ('MaintainWood', 'EnsureFoodSupply'):
            if facts.get('recovery', {}).get('roofHazard'):
                raise SkillBlocked('Roof-sensitive disruption: preserve reachable stock and sheltered work; outdoor acquisition and field expansion are deferred')
            food = goal_id == 'EnsureFoodSupply'
            from .food_capacity import choose_crop
            crop=choose_crop(facts) if food else None
            if food:
                facts=dict(facts,foodCrop=crop)
                goal.evidence['food_method']={'crop':crop,'biome':facts.get('biome'),
                    'climate':facts.get('foodClimate'),
                    'fallback':'Safe wild harvest and hunting when outdoor cropping is unavailable'}
            resource = 'food' if food else 'tree'
            targets = [p for p in facts.get('acquisition', []) if p[resource] and not p['designated']]
            if food:
                try:
                    targets, budget = acquisition_targets(facts, rt.controller.policy.food_target_days)
                except ValueError as error:
                    raise SkillBlocked('Food acquisition accounting: '+str(error)) from error
                goal.evidence['acquisition_budget'] = budget
            # A plant can regrow at the same location. Each fresh observation is
            # a new method, but uncertain or cancelled writes remain protected.
            protected = {(s.action.arguments.get('x'), s.action.arguments.get('z'))
                         for s in rt.current_plan.spec.steps
                         if s.goal_id == goal_id and s.action.kind == 'native_operation'
                         and s.action.tool in ('home/acquire_resource', 'rimworld/apply_architect_designator')
                         and (rt.current_plan.progress[s.id].state != 'complete'
                              or rt.current_plan.progress[s.id].issued.get('0', {}).get('confirmed') is not True)}
            targets = [p for p in targets if (p['x'], p['z']) not in protected]
            method = 'acquire-'+fingerprint({'plants':[p['id'] for p in targets[:8]], 'tick':facts['tick']})[:12]
            outstanding = sum(p.get('yield', 0) for p in facts.get('acquisition', []) if p[resource] and p['designated'])
            needed = max(0, rt.controller.policy.wood_target-facts.get('resources', {}).get('WoodLog', 0)-outstanding)
            if targets and unused(method) and (food or needed > 0):
                selected = []
                amount = 0
                for plant in targets:
                    if len(selected) >= 8 or (not food and amount >= needed): break
                    selected.append(native('home/acquire_resource',
                        **{k:rt.identity[k] for k in ('colonyId','loadToken','mapId')},
                        thingId=plant['id'], resource=plant['resource'], x=plant['x'], z=plant['z']))
                    amount += plant.get('yield', 0)
                if selected: return method, selected
            if not food:
                if not targets and outstanding == 0: raise SkillBlocked('No safe mature trees in acquisition radius')
                return None
            from .food_preservation import preservation_bill
            preservation=preservation_bill(facts,rt.controller.policy.food_target_days)
            if preservation:
                method='preserve-'+fingerprint(preservation)[:8]
                goal.evidence['preservation']=preservation
                if unused(method):
                    return method,[native('home/bills',action='add',bench=preservation['bench'].removeprefix('Thing_'),
                        recipe=preservation['recipe'],repeatMode='TargetCount',targetCount=preservation['targetCount'],
                        pauseWhenSatisfied='on',unpauseWhenYouHave=max(1,preservation['targetCount']//2),
                        ingredientSearchRadius=40,watch=False)]
            initial_method='rice' if crop=='Plant_Rice' else 'crop-'+str(crop)
            if crop and unused(initial_method) and sum(f.get('usableCells',0) for f in facts.get('farms',[]) if f.get('edible')) < facts['colonists']*10:
                layout = await self.layout(facts)
                patches=farm_patches(layout)
                goal.evidence['field_capacity']={'selected_cells':sum(p['width']*p['height'] for p in patches),
                    'minimum_growing_cells':facts['colonists']*10}
                if patches:
                    return initial_method, [{'kind': 'create_zone', 'zone_type': 'growing', 'label': 'RimGovernor food',
                                     'crop': crop, 'patches': patches}]
            if crop and (not unused(initial_method) or any(f.get('edible') and f.get('usableCells',0)>0
                    for f in facts.get('farms',[]))) and facts.get('indoorSleepingCapacity',0)>=facts['colonists']:
                from .capacity_growth import growth_fields
                from .food_capacity import field_target, growing_cells
                goal.evidence['production_budget'] = {
                    'target_days': rt.controller.policy.food_target_days,
                    'required_cells': field_target(facts,rt.controller.policy.food_target_days),
                    'observed_growing_cells': growing_cells(facts),
                    'scope': 'Planned harvest capacity; future food is not stored nutrition'}
                patches=growth_fields(rt.current_plan,facts,rt.controller.policy.food_target_days)
                method=initial_method+'-expand-'+fingerprint(patches)[:8]
                if patches and unused(method):
                    return method,[{'kind':'create_zone','zone_type':'growing','label':'RimGovernor '+method,
                        'crop':crop,'patches':patches}]
            if facts.get('foodRunwayDays', 0) < rt.controller.policy.food_target_days and facts.get('armed', 0):
                butcher = facts.get('butchering', [])
                if not butcher and unused('butcher-spot'):
                    from .development import placement
                    layout = await self.layout(facts)
                    return 'butcher-spot', [await placement(rt, facts, 'ButcherSpot', indoors=False,
                        near={'x': layout['room']['x'], 'z': layout['room']['z']}, goal=goal)]
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
            from .shelter_handoff import completed_shelters,sleeping_handoff
            shelters=list(completed_shelters(rt.current_plan))
            if shelters:
                identity,shell=shelters[0]
                method='player-sleep-'+str(facts['colonists'])
                if unused(method):return await sleeping_handoff(rt,facts,identity,shell)
                return None
            layout = await self.layout(facts)
            if unused('shell') and facts.get('indoorSleepingCapacity', 0) < facts['colonists']:
                if goal.method_epoch:
                    from .shelter_handoff import verified_room
                    if await verified_room(rt,self.shell(layout)) is None:return None
                else:
                    return 'shell', [self.shell(layout)]
            if facts['colonists']>8:
                method='starter-sleep-'+str(facts['colonists'])
                if not unused(method):
                    from .capacity_growth import grow_shelter
                    return await grow_shelter(self,facts) if facts.get('indoorSleepingCapacity',0)<facts['colonists'] else None
                room=layout['room']
                # Leave the service rows free for storage, cooking and temperature furniture.
                reserved={(x,z) for x in range(room['x']+1,room['x']+room['width']-1)
                          for z in range(room['z']+5,room['z']+room['height']-1)}
                try:
                    return await sleeping_handoff(rt,facts,None,self.shell(layout),reserved_cells=reserved,allow_partial=True)
                except SkillBlocked as error:
                    if 'insufficient verified sleeping space' not in str(error):raise
                    from .capacity_growth import grow_shelter
                    return await grow_shelter(self,facts)
            if unused('sleeping'):
                room = layout['room']
                placements = [{'def_name': 'SleepingSpot', 'x': room['x']+x, 'z': room['z']+z}
                              for z in (1, 3) for x in (1, 3, 5, 7)]
                return 'sleeping', [{'kind': 'place_buildings', 'placements': placements[:facts['colonists']]}]
            return None
        if goal_id == 'EnsureFoodStorage':
            if unused('storage'):
                from .shelter_handoff import player_shelter,furniture_handoff
                if selection:=player_shelter(rt.current_plan):
                    actions=await furniture_handoff(rt,selection)
                    return ('storage',actions) if actions else None
                if facts.get('indoorSleepingCapacity',0)<facts['colonists']:
                    return None
                layout = await self.layout(facts)
                from .shelter_handoff import furnish_room
                actions=await furnish_room(rt,self.shell(layout))
                return ('storage',actions) if actions else None
            return None
        if goal_id == 'EnsureCooking':
            benches = facts.get('cooking', [])
            usable = [bench for bench in benches if bench.get('usable') is True]
            if (not usable and not any(bench.get('defName') == 'Campfire' for bench in benches)
                    and unused('campfire')):
                from .shelter_handoff import player_shelter,furniture_handoff
                if selection:=player_shelter(rt.current_plan):
                    actions=await furniture_handoff(rt,selection,'Campfire')
                    return ('campfire',actions) if actions else None
                if benches:
                    return 'campfire', await self.cooking_fallback(facts)
                layout = await self.layout(facts)
                return 'campfire', [{'kind': 'place_buildings', 'placements': [{'def_name': 'Campfire',
                    'x': layout['room']['x']+6, 'z': layout['room']['z']+6}]}]
            for bench in usable:
                if bench.get('recipes') and unused('bill'):
                    recipe = 'CookMealSimple' if 'CookMealSimple' in bench['recipes'] else sorted(bench['recipes'])[0]
                    return 'bill', [native('home/bills', action='add', bench=bench['id'].removeprefix('Thing_'), recipe=recipe,
                        repeatMode='TargetCount', targetCount=facts['colonists']*3,
                        unpauseWhenYouHave=facts['colonists'], pauseWhenSatisfied='on', ingredientSearchRadius=40, watch=False)]
            return None
        if goal_id == 'EnsureTemperatureSafety':
            if unused('thermal'):
                from .shelter_handoff import player_shelter,furniture_handoff,verified_room
                if selection:=player_shelter(rt.current_plan):
                    # Safe-reachability bed counts can disappear in an unsafe room.
                    # Heating/cooling must use that room's actual temperature.
                    observed=await verified_room(rt,selection[2])
                    if observed is None:return None
                    temperature=observed[0].get('temperature')
                    if not isinstance(temperature,(int,float)):
                        raise SkillBlocked('Selected shelter temperature is unavailable')
                    cold=temperature < rt.controller.policy.temperature_enter_low
                    if not cold and temperature <= rt.controller.policy.temperature_enter_high:return None
                    definition='Campfire' if cold else 'PassiveCooler'
                    actions=await furniture_handoff(rt,selection,definition)
                    return ('thermal',actions) if actions else None
                if facts.get('indoorSleepingCapacity', 0) < facts['colonists']: return None
                cold = facts.get('sleepingTemperatureMin', 20) < rt.controller.policy.temperature_enter_low
                definition = 'Campfire' if cold else 'PassiveCooler'
                layout = await self.layout(facts)
                room = layout['room']
                if cold and any(b.get('defName') == 'Campfire' and b.get('usable') is True
                                and room['x'] < b.get('position', {}).get('x', -1) < room['x']+room['width']-1
                                and room['z'] < b.get('position', {}).get('z', -1) < room['z']+room['height']-1
                                for b in facts.get('cooking', [])): return None
                return 'thermal', [{'kind': 'place_buildings', 'placements': [{'def_name': definition,
                    'x': layout['room']['x']+2, 'z': layout['room']['z']+6}]}]
            return None
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
            identity = (('herd-' if goal_id.startswith('MaintainHerd-') else 'resource-') + fingerprint({'goal':goal_id,'attempt':goal.attempts,'method':method,'index':index})[:32]
                        if goal_id.startswith(('MaintainResource-', 'MaintainHerd-')) else f'{goal_id}-{goal.attempts}-{method}-{index}'[:64])
            step = PlanStep(id=identity, title=f'{goal_id}: {method}', goal_id=goal_id, source=goal.source,
                priority=max(75 if goal.source=='PLAYER' else 0,100-goal.priority_class*20), action=action,
                completion_criteria='Native effect observed; colony goal separately verifies functional postconditions')
            if getattr(step.action, 'completion', None) == 'upkeep_target':
                goal.evidence.setdefault('upkeep_orders', {})[identity] = dict(goal.evidence['upkeep_order']['target'])
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
                    if definition and definition.get('available') is False:
                        required = goal.evidence.setdefault('required_capabilities', [])
                        if placement.def_name not in required: required.append(placement.def_name)
                    raise SkillBlocked('No available observed construction definition: '+placement.def_name)
                material = placement.materials[0] if placement.materials else definition.get('stuff')
                if material != definition.get('stuff') and material not in definition['costs']:
                    observed = goal.evidence.get('construction_costs', {}).get(placement.def_name, {}).get(material)
                    if not observed:
                        raise SkillBlocked('Native costs for the selected construction material are unavailable')
                    costs[str(slot)] = observed
                else:
                    costs[str(slot)] = definition['costs']
            slots[identity] = costs
            result.append(step)
        return result, slots
