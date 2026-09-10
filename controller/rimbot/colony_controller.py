"""Deterministic priority tree using ColonyPlan, native validation and Hands."""
from .production_policy import refresh_resource_prerequisite
from dataclasses import asdict
from .colony_plan import ColonyGoal, CommitSteps
from .colony_policy import required_colony_work, ColonyPolicy, allocation, criteria, derive, priority_nodes, work_assignment
from .colony_skills import ColonySkills, SkillBlocked
from .config import ModelRole
from .strategic_state import fingerprint
from .development_priorities import arbitrate, release_admission


class ColonyController:
    def __init__(self, rt):
        self.rt = rt
        self.policy = ColonyPolicy()
        self.skills = ColonySkills(rt)

    def event(self, kind, goal_id, **evidence):
        self.rt.note(kind, goal_id, goal_id=goal_id, **evidence)

    def finish_review(self):
        rt = self.rt
        rt.handled_revision = rt.chat_revision
        rt.strategic_state.decided()
        rt.wake.clear()
        rt.persist()

    async def cycle(self):
        rt = self.rt
        if rt.mode != 'automate':
            self.finish_review()
            return
        token, direction = rt.context_token, rt.chat_revision
        plan = rt.current_plan
        policy_values = dict(asdict(ColonyPolicy()), **plan.control.get('policy', {}))
        if any(key.startswith('Population-') and not goal.cancelled and goal.status != 'complete'
               for key, goal in plan.colony_goals.items()):
            population_days = plan.control.get('population_policy', {}).get('food_days', 0)
            policy_values['food_target_days'] = max(policy_values['food_target_days'], population_days)
            policy_values['food_min_days'] = min(policy_values['food_target_days'] - .1,
                max(policy_values['food_min_days'], population_days * .9))
        self.policy = ColonyPolicy(**policy_values)
        native = await rt.game.query('home/colony_facts', planning=True)
        people = await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True, equipment=True, needs=True, thoughts=True, schedule=True)
        await rt.ensure_context(token)
        if direction != rt.chat_revision: return
        if native.get('success') is not True:
            raise ValueError('Deterministic state unavailable: '+str(native.get('error')))
        facts = derive(rt.batch, native, self.policy)
        if 'MaintainWaste' not in plan.colony_goals and native.get('waste'):
            from .waste_management import pending_items
            if pending_items(native['waste']):
                plan.colony_goals['MaintainWaste'] = ColonyGoal(priority_class=3,
                    started_tick=facts['tick'], last_progress_tick=facts['tick'])
                plan.control['waste'] = native['waste']
                self.event('goal_created', 'MaintainWaste', priority_class=3)
        waste_goal = plan.colony_goals.get('MaintainWaste')
        if waste_goal and not waste_goal.target.get('unwanted') and not waste_goal.target.get('bury') and native.get('waste'):
            plan.control['waste'] = native['waste']
        from .mood_control import assess, priority_nodes as mood_nodes
        facts['mood'] = assess(people.get('pawns', []) if people.get('success') is not False else [],
            facts['forecasts']['people'], plan.control.get('facts', {}).get('mood', {}))

        from .medical_management import care_state
        facts['longTermMedical'] = care_state(people['pawns'], plan.control.get('facts', {}).get('longTermMedical'))
        managed=set(plan.control.get('combat',{}).get('pawns',[]))
        managed.update(s.action.arguments.get('pawn') for s in plan.spec.steps
            if s.source=='AUTOPILOT' and s.action.kind=='native_operation'
            and s.action.tool=='home/order' and s.action.arguments.get('action')=='tend'
            and plan.progress[s.id].state=='complete')
        facts['cleanupPawns'] = sorted(p for p in managed
            if getattr(rt,'draft_owners',{}).get(p)==token and p not in plan.control.get('player_draft_overrides',{}))
        # Claim only the initially observed starter supplies. Once a cell is
        # allowed, later player forbidding must not restart an allow loop.
        pending_supplies = plan.control.setdefault('starting_supplies',facts.get('forbiddenSupplies',[]))
        still_forbidden = {(p['x'],p['z']) for p in facts.get('forbiddenSupplies',[])}
        pending_supplies = [p for p in pending_supplies if (p['x'],p['z']) in still_forbidden]
        plan.control['starting_supplies'] = pending_supplies
        facts['forbiddenSupplies'] = pending_supplies
        from .husbandry import refresh_husbandry, required_handler_skill
        herd_nodes = await refresh_husbandry(rt, native)
        await rt.ensure_context(token)
        if direction != rt.chat_revision or rt.mode != 'automate': return
        assignments, coverage = work_assignment(people['pawns'], required_colony_work(plan), plan.control.get('work_overrides', {}), required_handler_skill(plan))
        for pawn, values in plan.control.get('work_overrides', {}).items():
            if pawn in assignments: assignments[pawn].update(values)
        coverage = coverage and all(any(values.get(work, 0) > 0 for values in assignments.values())
                                    for work in {'Doctor', 'Cooking', 'Construction', 'Growing', *required_colony_work(plan)})
        by_id = {p['thingId']: p for p in people['pawns']}
        facts['workCoverage'] = coverage and all(
            all(any(w['name'] == name and (w.get('priorityStored') == priority if
                by_id[pawn]['work'].get('manualPriorities') else (w.get('priority', 0) > 0) == (priority > 0))
                for w in by_id[pawn]['work']['types']) for name, priority in work.items())
            for pawn, work in assignments.items())
        resource_nodes = []
        for identity, goal in plan.colony_goals.items():
            if identity.startswith('MaintainResource-') and not goal.cancelled:
                if await refresh_resource_prerequisite(rt, identity, facts):
                    self.event('goal_resumed', identity, reason='Native resource prerequisite became available')
                await rt.ensure_context(token)
                if direction != rt.chat_revision or rt.mode != 'automate': return
                stock = facts.get('resources', {}).get(goal.target['resource'], 0) if goal.target['resource'] in facts.get('policyResources', {}) else None
                goal.evidence['stock'] = stock
                goal.evidence['deficit'] = None if stock is None else max(0, goal.target['quantity'] - stock)
                if stock is None or stock < goal.target['quantity']:
                    resource_nodes.append((identity, 3))
        old_latches = dict(plan.control.get('latches', {}))
        from .population import refresh as refresh_population
        population_nodes = await refresh_population(rt, facts, people['pawns'])
        await rt.ensure_context(token)
        if direction != rt.chat_revision or rt.mode != 'automate': return
        nodes = priority_nodes(facts, plan.control.setdefault('latches', {}), self.policy) + resource_nodes + population_nodes + herd_nodes
        nodes += mood_nodes(facts['mood'])
        nodes.sort(key=lambda node: node[1])
        waste = plan.colony_goals.get('MaintainWaste')
        if waste and not waste.cancelled:
            from .waste_management import pending_items
            pending = pending_items(plan.control.get('waste', {}))
            waste.evidence['observation'] = plan.control.get('waste', {})
            unresolved = any(plan.progress[s].state not in ('complete', 'cancelled') for s in waste.steps if s in plan.progress)
            if pending is None or pending or unresolved:
                nodes.append(('MaintainWaste', 3))
            if (waste.status == 'blocked' and not waste.evidence.get('watchdog')
                    and not any(plan.progress[s].state == 'blocked' for s in waste.steps if s in plan.progress)):
                waste.status, waste.reason = 'active', ''
            facts['waste'] = plan.control.get('waste', {}).get('items')
        from .disaster_recovery import reconcile, prioritize
        recovery = reconcile(plan.control, facts, self.policy, context=token, direction=direction)
        nodes = prioritize(nodes, recovery)
        for name, value in plan.control['latches'].items():
            if old_latches.get(name) != value:
                self.event('hysteresis_changed', name, active=value)
        gates = criteria(facts, self.policy)
        from .spatial_program import stage_layout
        nodes = stage_layout(plan, facts, gates, nodes)
        plan.control['facts'] = {k: v for k, v in facts.items() if k not in ('cells', 'definitions')}
        plan.control['criteria'] = gates
        applicable = dict(nodes)
        plan.control.pop('execution_hold',None)
        if not facts.get('hostiles'):
            for step in plan.spec.steps:
                progress = plan.progress[step.id]
                if progress.state=='blocked' and progress.failure and progress.failure.code=='starting_supplies_unavailable':
                    from .supply_recovery import recover_starting_supplies
                    await recover_starting_supplies(rt,step.id,token=token,direction=direction)
                    if rt.context_token!=token or rt.chat_revision!=direction or rt.mode!='automate':return
                if (progress.state=='blocked' and progress.failure
                        and progress.failure.code in ('construction_resources','construction_unavailable')):
                    from .construction_recovery import recover_construction
                    await recover_construction(rt,step.id,token=token,direction=direction,
                        limit=self.policy.max_method_attempts)
                    if rt.context_token!=token or rt.chat_revision!=direction or rt.mode!='automate':return
                if (step.source == 'AUTOPILOT' and step.goal_id == 'CriticalMedical'
                        and progress.state == 'blocked' and progress.failure
                        and progress.failure.code in ('tending_interrupted', 'doctor_unavailable') and progress.failure.retryable):
                    await rt.recover_medical(step.id, expected_token=token, expected_revision=direction,
                                             limit=self.policy.max_method_attempts)
                    if rt.context_token != token or rt.chat_revision != direction or rt.mode != 'automate': return
        player_work = set()
        for identity, goal in plan.colony_goals.items():
            if not identity.startswith('intent-') or goal.cancelled: continue
            if goal.evidence.get('request',{}).get('kind')=='AdoptRoom':
                from .room_adoption import validate_adoption
                from .shelter_handoff import requested_shell,verified_room
                try:
                    await validate_adoption(rt,goal)
                    observed=await verified_room(rt,requested_shell(goal.evidence['request']))
                    await rt.ensure_context(token)
                    if direction!=rt.chat_revision:return
                    if observed is None:
                        goal.status,goal.reason='active','Waiting for adopted room roof coverage'
                        player_work.add('EnsureInitialShelter')
                    else:
                        goal.status,goal.reason='complete',''
                except SkillBlocked as error:
                    goal.status,goal.reason='blocked',str(error)
                    player_work.add('EnsureInitialShelter')
                continue
            states = [plan.progress[s] for s in goal.steps if s in plan.progress]
            if states and all(p.state=='complete' for p in states):
                goal.status = 'complete'
            elif any(p.state=='blocked' for p in states):
                goal.status, goal.reason = 'blocked', 'Player construction needs attention; inspect its action failure'
                player_work.add(goal.target.get('satisfies'))
            else:
                player_work.add(goal.target.get('satisfies'))
        preferred=plan.colony_goals.get(plan.control.get('preferred_shelter'))
        if preferred and not preferred.cancelled and preferred.status=='complete':
            player_work.discard('EnsureInitialShelter')
        minimum = min(applicable.values(), default=4)
        for identity, priority in nodes:
            goal = plan.colony_goals.get(identity)
            if goal is None:
                goal = plan.colony_goals[identity] = ColonyGoal(priority_class=priority,
                    started_tick=facts['tick'], last_progress_tick=facts['tick'])
                self.event('goal_created', identity, priority_class=priority)
            if identity in plan.control.get('suppressed_goals',{}):
                goal.cancelled, goal.status, goal.reason = True, 'blocked', 'Related player intent was cancelled'
            if goal.cancelled: continue
            if identity == 'MaintainEquipment':
                gear = dict(facts.get('gearUpkeep') or {})
                gear.pop('tick', None)
                gear_signature = fingerprint(gear)
                if (goal.status == 'blocked' and all(plan.progress[s].state == 'complete' for s in goal.steps)
                        and goal.evidence.get('availability') != gear_signature):
                    goal.status, goal.reason = 'active', ''
                    goal.evidence.pop('watchdog', None)
                    goal.last_progress_tick = facts['tick']
                goal.evidence['availability'] = gear_signature
            if goal.status == 'complete':
                goal.status = 'active'
                goal.reopen_methods()
                if identity.startswith('EnsureMood-'):
                    goal.evidence.pop('need_high_water', None)
                goal.attempts += 1
                goal.last_progress_tick = facts['tick']
                self.event('goal_reopened', identity)
            if identity.startswith('EnsureMood-'):
                from .strategic_state import fingerprint as mood_fingerprint
                state = facts['mood'][identity.removeprefix('EnsureMood-')]
                observed = mood_fingerprint({k: state.get(k) for k in
                    ('known', 'mentalState', 'causes', 'missing', 'schedule', 'playerForced', 'drafted', 'downed')})
                review_window = facts['tick'] // 2500
                if goal.status == 'blocked' and (goal.evidence.get('mood_observation') != observed
                        or goal.evidence.get('mood_review_window') != review_window):
                    goal.status, goal.reason = 'active', ''
                goal.evidence['mood_observation'] = observed
                goal.evidence['mood_review_window'] = review_window
            if identity.startswith('EnsureMood-') or (identity in ('MaintainWood', 'EnsureFoodStorage') and goal.source == 'AUTOPILOT'):
                goal.priority_class = priority
            else:
                goal.priority_class = min(goal.priority_class, priority)
            progress_fields = {
                'MaintainWaste': ['waste'],
                'EnsureFoodSupply': ['foodNutrition'], 'MaintainWood': ['resources'],
                'EnsureInitialShelter': ['bedCapacity', 'indoorSleepingCapacity'],
                'EnsureCooking': ['cooking'], 'EnsureFoodStorage': ['foodStorage'],
                'EnsureWorkAssignments': ['workCoverage'], 'EnsureBasicDefense': ['armed'],
                'MaintainEquipment': [],
                'CriticalMedical': ['criticalPatients'], 'ActiveCombat': ['hostiles'],
                'MaintainMedicalCare': ['longTermMedical'],
                'EnsureTemperatureSafety': ['sleepingTemperatureMin', 'sleepingTemperatureMax'],
                'EnsureBasicPower': ['powerHeadroom'], 'AllowStartingSupplies': ['forbiddenSupplies']}
            progress_facts = {key: facts.get(key) for key in progress_fields.get(identity,
                ['resources'] if identity.startswith('MaintainResource-') else [])}
            if identity.startswith('EnsureMood-'):
                best = goal.evidence.setdefault('need_high_water', {})
                for cause in facts['mood'][identity.removeprefix('EnsureMood-')]['causes']:
                    level = cause['level']
                    if level is not None and level > best.get(cause['need'], -1) + .01:
                        best[cause['need']] = level
                progress_facts = dict(best)
            signature = fingerprint({'facts': progress_facts,
                'herd': ({'population': goal.evidence['husbandry'].get('population'),
                    'training': [(a['id'], [(r['name'], r.get('learned'), r.get('stepsDone')) for r in a['training']])
                                 for a in goal.evidence['husbandry'].get('animals', [])],
                    'pregnancies': [(a['id'], a.get('gestation')) for a in goal.evidence['husbandry'].get('animals', []) if a.get('pregnant')]}
                         if identity.startswith('MaintainHerd-') else None),
                'steps': {s: plan.progress[s].state for s in goal.steps if s in plan.progress}})
            if signature != goal.evidence.get('progress'):
                goal.evidence['progress'] = signature
                goal.last_progress_tick = facts['tick']
            watchdog = goal.evidence.get('watchdog')
            if (goal.status == 'blocked' and watchdog and goal.reason == watchdog.get('reason')
                    and facts['tick'] >= watchdog['tick']):
                completed = {s for s in goal.steps if s in plan.progress and plan.progress[s].state == 'complete'}
                failed = any(plan.progress[s].state in ('blocked', 'cancelled')
                             for s in goal.steps if s in plan.progress)
                if completed - set(watchdog['completed_steps']) and not failed:
                    # Native labor can finish while the controller holds further
                    # orders. Resume the existing method, preserving its receipts.
                    goal.status, goal.reason = 'active', ''
                    goal.last_progress_tick = facts['tick']
                    goal.evidence.pop('watchdog', None)
                    self.event('goal_resumed', identity, reason='Tracked work completed after watchdog hold')
            # Emergency work suspends development without erasing issued orders.
            suspended = minimum < 2 and goal.priority_class > minimum
            if suspended and goal.status == 'active':
                goal.status = 'suspended'
                self.event('goal_suspended', identity)
            elif not suspended and goal.status == 'suspended':
                goal.status = 'active'
                self.event('goal_resumed', identity)
            if goal.status == 'blocked' and goal.reason.startswith('No available observed construction definition:'):
                required = goal.evidence.get('required_capabilities', [])
                if required and all(facts.get('definitions', {}).get(name, {}).get('available') is True for name in required):
                    goal.status, goal.reason = 'active', ''
                    self.event('goal_resumed', identity, reason='Native construction research is now available')
            if goal.status == 'blocked' and goal.reason.startswith('Resources:'):
                if facts.get('resources') != goal.evidence.get('blocked_stock'):
                    goal.status, goal.reason = 'active', ''
                    self.event('goal_resumed', identity)
        from .research import refresh as refresh_research
        research_nodes = await refresh_research(rt, facts, people['pawns'], nodes)
        await rt.ensure_context(token)
        if direction != rt.chat_revision: return
        nodes += research_nodes
        applicable.update(research_nodes)
        if minimum < 2 and research_nodes and plan.colony_goals['EnsureResearch'].status == 'active':
            plan.colony_goals['EnsureResearch'].status = 'suspended'
        for identity, goal in plan.colony_goals.items():
            if identity not in applicable and not identity.startswith('intent-') and goal.source != 'LLM_ADVISOR' and not goal.cancelled and goal.status != 'complete':
                goal.status = 'complete'
                self.event('goal_completed', identity, criteria=gates)
        for identity,priority in nodes:
            emergency=plan.colony_goals[identity]
            if priority<2 and emergency.status=='blocked' and not emergency.cancelled:
                plan.control['execution_hold']=emergency.reason
        stable = bool(gates) and all(gates.values())
        if stable != (plan.control.get('status') == 'FOOTHOLD_STABLE'):
            self.event('bootstrap_stability_reached' if stable else 'bootstrap_stability_lost', 'EstablishFoothold', criteria=gates)
        plan.control['status'] = 'FOOTHOLD_STABLE' if stable else 'ESTABLISHING_FOOTHOLD'
        plan.control['simulation_needed'] = stable
        nodes, development_admitted = arbitrate(plan, facts, people['pawns'], nodes, self.policy,
                                                context=token, direction=direction)
        rt.persist()
        for identity, node_priority in nodes:
            goal = plan.colony_goals[identity]
            if goal.status != 'active' or goal.cancelled: continue
            if identity in player_work:
                goal.reason = 'Waiting for accepted player work serving this goal'
                plan.control['simulation_needed'] = True
                continue
            if goal.reason == 'Waiting for accepted player work serving this goal': goal.reason = ''
            existing = [plan.progress[s] for s in goal.steps if s in plan.progress]
            failed = next((p.failure for p in existing if p.state == 'blocked'), None)
            if failed:
                recovery = goal.evidence.get('recovery', {})
                self.block(goal, identity, recovery.get('message', failed.detail)
                           if identity == 'CriticalMedical' and failed.code == 'tending_interrupted'
                           and recovery.get('state') == 'blocked' else failed.detail)
                continue
            timeout = 3000 if identity=='ActiveCombat' else 600000 if identity.startswith('Population-') else self.policy.blocked_after_ticks
            if ((goal.steps or identity.startswith('Population-')) and facts['tick'] - goal.last_progress_tick >= timeout
                    and (identity != 'MaintainMedicalCare' or any(p.state != 'complete' for p in existing))):
                reason = f'No measurable progress within {timeout} game ticks; inspect labor/materials/postconditions'
                goal.evidence['watchdog'] = dict(tick=facts['tick'], reason=reason,
                    completed_steps=[s for s in goal.steps if s in plan.progress and plan.progress[s].state == 'complete'])
                self.block(goal, identity, reason)
                release_admission(plan, identity, development_admitted, reason)
                continue
            if any(p.state in ('pending', 'executing', 'waiting') for p in existing):
                plan.control['simulation_needed'] = True
                continue
            if node_priority >= 3 and identity not in development_admitted:
                continue
            try:
                compiled = await self.skills.compile(identity, facts, people['pawns'])
                if compiled is None:
                    release_admission(plan, identity, development_admitted, 'Existing method awaiting native progress')
                    if identity.startswith('Population-') and goal.status != 'complete':
                        plan.control['simulation_needed'] = True
                    existing_process = (identity=='CriticalMedical' and any(p.get('job')=='TendPatient' or
                        ((p.get('health') or {}).get('shouldSeekMedicalRest') is True and
                         (p.get('health') or {}).get('inBed') is True) for p in people['pawns'])) or (identity=='EnsureFoodSupply' and any(f.get('growingCells',0)>0 for f in facts.get('farms',[]))) or (
                        identity in ('EnsureFoodSupply','MaintainWood') and any(p.get('designated') for p in facts.get('acquisition',[])))
                    if goal.evidence.get('methods') or goal.archived_methods or existing_process or identity.startswith(('MaintainResource-', 'MaintainHerd-')) or identity in ('EnsureResearch', 'MaintainMedicalCare'): plan.control['simulation_needed'] = True
                    continue
                method, actions = compiled
                steps, slots = self.skills.steps(identity, method, actions, facts)
                cost = {}
                for costs in slots.values():
                    for values in costs.values():
                        for resource, count in values.items(): cost[resource] = cost.get(resource, 0) + count
                deficits = allocation(plan, facts, cost, self.policy, survival=goal.priority_class <= 2)
                if deficits:
                    self.event('resource_reservation_rejected', identity, deficits=deficits)
                    goal.evidence['blocked_stock'] = facts['resources']
                    if 'WoodLog' in deficits:
                        plan.control['latches']['wood'] = True
                    raise SkillBlocked('Resources: '+str(deficits))
                self.event('htn_method_selected', identity, method=method)
                decision = CommitSteps(expected_revision=plan.revision,
                    reason=f'{identity}: {method}', steps=steps).decision(plan)
                await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST,
                    expected_token=token, expected_revision=direction)
                if identity=='ActiveCombat':
                    participants=set(plan.control.get('combat',{}).get('pawns',[]))
                    participants.update(a['arguments']['pawn'] for a in actions)
                    plan.control['combat']={'target':goal.evidence['combat_target'],
                        'pawns':sorted(participants), 'steps':[s.id for s in steps]}
                goal.method = method
                goal.steps.extend(s.id for s in steps)
                goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                plan.control.setdefault('costs', {}).update(slots)
                goal.last_progress_tick = facts['tick']
                self.event('skill_started', identity, method=method, steps=[s.id for s in steps])
                # One small commitment per cycle; Hands gets the next turn.
                rt.persist()
                return
            except SkillBlocked as error:
                self.block(goal, identity, str(error))
                release_admission(plan, identity, development_admitted, str(error))
            except ValueError as error:
                if rt.context_token != token or rt.chat_revision != direction: return
                # Validation failed before dispatch. Preserve all prior effects;
                # never silently relocate a partially designated structure.
                goal.attempts += 1
                self.block(goal, identity, str(error))
                release_admission(plan, identity, development_admitted, str(error))
        self.finish_review()

    def block(self, goal, identity, reason):
        goal.status, goal.reason = 'blocked', reason
        if goal.priority_class<2:
            self.rt.current_plan.control['execution_hold']=reason
        self.event('goal_blocked', identity, reason=reason)
        self.rt.persist()
