"""Persistent player budgets projected onto native bill job admission."""
from .strategic_state import fingerprint
from .world_progression import caravan_cargo_held


def production_budgets(plan):
    floors, stopped = {}, []
    for resource, policy in plan.control.get('resource_policy', {}).items():
        floors[resource] = policy.get('reserve', 0)
        if policy.get('spending', 'normal') != 'normal': stopped.append(resource)
    for identity, slots in plan.control.get('costs', {}).items():
        progress = plan.progress.get(identity)
        cargo_held = caravan_cargo_held(plan, identity)
        if progress is None or (progress.state in ('complete', 'cancelled', 'blocked') and not cargo_held): continue
        for slot, costs in slots.items():
            if progress.issued.get(slot, {}).get('confirmed') and not cargo_held: continue
            for resource, count in costs.items(): floors[resource] = floors.get(resource, 0) + count
    return {k: v for k, v in floors.items() if v}, sorted(stopped)


def policy_arguments(rt):
    floors, stopped = production_budgets(rt.current_plan)
    reserves = {k:p.get('reserve', 0) for k,p in rt.current_plan.control.get('resource_policy', {}).items() if p.get('reserve', 0)}
    holds = {k:v-reserves.get(k,0) for k,v in floors.items() if v > reserves.get(k,0)}
    from .extraction_development import drilling_policy
    drilling = drilling_policy(rt.current_plan)
    return {**{k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')},
            **({'drills': drilling} if drilling else {}),
            'floors': ','.join(f'{k}={v}' for k, v in sorted(reserves.items())),
            'commitments': ','.join(f'{k}={v}' for k, v in sorted(holds.items())),
            'stopped': ','.join(stopped), 'dryRun': False}


async def sync_production_policy(rt):
    """Called while holding the runtime writer lock, before starting simulation."""
    args = policy_arguments(rt)
    signature = fingerprint(args)
    # Native policies can outlive a controller database: even an empty snapshot
    # must clear the current map's old budgets after loading or reconnecting.
    if getattr(rt, '_production_policy_signature', None) == signature: return
    token, direction, revision = rt.context_token, rt.chat_revision, rt.current_plan.revision
    rt.current_plan.control['production_policy_dispatch'] = {'arguments': args, 'confirmed': False}
    rt.persist()
    result = await rt.game.invoke('home/production_policy', args, allow_write=True)
    await rt.sync_identity()
    if (rt.context_token != token or rt.chat_revision != direction or rt.current_plan.revision != revision):
        raise InterruptedError('Direction or colony changed while applying production policy; clock remains paused')
    expected = {k:dict((part.split('=')[0], int(part.split('=')[1])) for part in args[k].split(',') if part) for k in ('floors','commitments')}
    if result.get('success') is not True or any(result.get(k) != v for k,v in expected.items()):
        raise ValueError('Native production policy was not verified; clock remains paused')
    if args.get('drills') and result.get('drills') != args['drills']:
        raise ValueError('Native bounded extraction policy was not verified; clock remains paused')
    if sorted(result.get('stopped', [])) != production_budgets(rt.current_plan)[1]:
        raise ValueError('Native stopped production inputs were not verified')
    rt.current_plan.control['production_policy_dispatch'] = {'arguments': args, 'confirmed': True, 'receipt': result}
    rt._production_policy_signature = signature
    rt.persist()


def ingredient_deficits(recipe, resources):
    """Native per-definition recipe quantities, including alternatives; no fixed game tables."""
    result = []
    for row in recipe.get('ingredients', []):
        choices = row.get('costOptions')
        if choices is None or row.get('unreadable'):
            raise ValueError('Exact native ingredient quantities unavailable')
        result.append([{'resource': c['defName'], 'required': c['needed'],
                        'deficit': max(0, c['needed'] - resources.get(c['defName'], 0))}
                       for c in choices])
    return result


def required_resource_work(plan):
    result = {row['name']: next(iter(row.get('skills', [])), None)
              for key, goal in plan.colony_goals.items()
              if (key.startswith('MaintainResource-') or key in ('MaintainEquipment', 'MaintainMedicalReserves')) and not goal.cancelled and goal.status != 'complete'
              for row in goal.evidence.get('work_types', [])}
    research = plan.colony_goals.get('EnsureResearch')
    if research and not research.cancelled and research.status != 'complete' and research.evidence.get('research', {}).get('queue'):
        result['Research'] = 'Intellectual'
    if any(key.startswith('MaintainHerd-') and not goal.cancelled
           for key, goal in plan.colony_goals.items()):
        result['Handling'] = 'Animals'
    return result


def observe_mining_progress(goal, sources):
    progress = {s['thingId']: s['hitPoints'] for s in sources.get('sources', [])
                if s.get('method') == 'mine' and s.get('designated') and isinstance(s.get('hitPoints'), int)}
    previous = goal.evidence.get('mining_progress', {})
    advanced = (isinstance(sources.get('tick'), int) and sources['tick'] >= goal.last_progress_tick
                and any(identity in previous and hp < previous[identity] for identity, hp in progress.items()))
    infrastructure = sources.get('infrastructure') or {}
    owned = {r.get('thingId') for r in infrastructure.get('owned', []) if r.get('thingId')}
    drilling = {d['thingId']: d['progress'] for d in infrastructure.get('drills', [])
                if d['thingId'] in owned and isinstance(d.get('progress'), (int, float))}
    prior_drilling = goal.evidence.get('drilling_progress', {})
    advanced |= (isinstance(sources.get('tick'), int) and sources['tick'] >= goal.last_progress_tick
                 and any(identity in prior_drilling and value > prior_drilling[identity]
                         for identity, value in drilling.items()))
    if advanced: goal.last_progress_tick = sources['tick']
    goal.evidence['mining_progress'] = progress
    goal.evidence['drilling_progress'] = drilling
    return advanced


async def refresh_resource_progress(rt, goal_id):
    """Read actual excavation work before the shared no-progress watchdog runs."""
    goal = rt.current_plan.colony_goals[goal_id]
    if not (goal.evidence.get('mining_progress') or goal.target.get('deep_extraction')): return False
    token, direction = rt.context_token, rt.chat_revision
    sources = await rt.game.invoke('home/resource_sources', {'resource': goal.target['resource']})
    if (rt.context_token != token or rt.chat_revision != direction
            or rt.current_plan.colony_goals.get(goal_id) is not goal
            or sources.get('success') is not True
            or any(sources.get(k) != rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId'))):
        return False
    advanced = observe_mining_progress(goal, sources)
    watchdog = goal.evidence.get('watchdog')
    if advanced and goal.status == 'blocked' and watchdog and goal.reason == watchdog['reason']:
        goal.status, goal.reason = 'active', ''
        goal.evidence.pop('watchdog')
        return True
    return False


async def resource_method(rt, goal_id, facts):
    from .colony_skills import SkillBlocked, native
    goal = rt.current_plan.colony_goals[goal_id]
    resource, target = goal.target['resource'], goal.target['quantity']
    stock = facts.get('resources', {}).get(resource, 0) if resource in facts.get('policyResources', {}) else None
    if stock is None: raise SkillBlocked('Resource stock is unavailable: ' + resource)
    goal.evidence['stock'] = stock
    goal.evidence['deficit'] = max(0, target - stock)
    if stock >= target: return None
    sources = await rt.game.invoke('home/resource_sources', {'resource': resource,
        **({'development': True} if goal.target.get('deep_extraction') else {})})
    if sources.get('success') is not True: raise SkillBlocked('Native resource sources unavailable')
    if any(key in sources and sources[key] != rt.identity[key] for key in ('colonyId', 'loadToken', 'mapId')):
        raise SkillBlocked('Native resource sources unavailable: colony/load/map changed')
    goal.evidence['acquisition_blockers'] = sources.get('blocked', [])
    goal.evidence['extractions'] = sources.get('extractions', [])
    goal.evidence['extraction_infrastructure'] = sources.get('infrastructure')
    rows = sources.get('sources', [])
    observe_mining_progress(goal, sources)
    # An older companion cannot certify excavation geometry.
    rows = [s for s in rows if s.get('method') != 'mine' or s.get('safety') == 'open_surface']
    pending = sources.get('pendingYield', sum(s['yield'] for s in rows if s.get('designated')))
    goal.evidence['pending_acquisition'] = pending
    needed = max(0, target - stock - pending)
    selected = []
    for source in sorted(rows, key=lambda s: (s.get('distance', 0), s['thingId'])):
        if needed <= 0 or len(selected) == 8: break
        if source.get('designated') or source.get('yield', 0) <= 0: continue
        if source.get('method') == 'mine' and selected: break
        selected.append(source); needed -= source['yield']
        # One excavation identity per method preserves cancellation across changing
        # stock targets without retaining an unbounded second source ledger.
        if source.get('method') == 'mine': break
    if selected or pending:
        goal.evidence['work_types'] = [w for source in rows
            if source in selected or source.get('designated') for w in source.get('workTypes', [])]
    if selected:
        method = 'acquire-' + fingerprint([s.get('sourceId', s['thingId']) for s in selected])[:12]
        if goal.method_seen(method):
            raise SkillBlocked('Previously issued extraction was interrupted; explicitly renew the resource goal after inspection')
        if any(s.get('method') == 'mine' for s in selected) and 'storage' in sources:
            storage = sources['storage']
            goal.evidence['material_storage'] = storage
            if not storage.get('haulers'):
                raise SkillBlocked('Material storage unavailable: no eligible hauler')
            goal.evidence['work_types'].append(storage['workType'])
            capacity_needed = sum(s['yield'] for s in selected) + pending
            if storage['capacity'] < capacity_needed:
                import math
                cells = storage['candidates'][:math.ceil((capacity_needed - storage['capacity']) / storage['stackLimit'])]
                if not cells:
                    raise SkillBlocked('Material storage unavailable: no safe free storage space')
                storage_method = 'material-storage-' + fingerprint({'resource': resource, 'cells': cells})[:12]
                if goal.method_seen(storage_method):
                    raise SkillBlocked('Material storage unavailable: previously issued storage changed; inspection required')
                return storage_method, [{'kind': 'create_zone', 'zone_type': 'stockpile', 'label': 'RimBot ' + storage_method,
                    'preset': 'nothing', 'allow': [resource], 'priority': 'Important',
                    'patches': [dict(c, width=1, height=1) for c in cells]}]
        goal.evidence['selected_sources'] = [{k: s[k] for k in ('thingId', 'resource', 'x', 'z', 'yield')} for s in selected]
        goal.evidence['mining_progress'].update({s['thingId']: s['hitPoints'] for s in selected
            if s.get('method') == 'mine' and isinstance(s.get('hitPoints'), int)})
        return method, [native('home/acquire_resource',
            **{k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')},
            **{k: s[k] for k in ('thingId', 'resource', 'x', 'z')}) for s in selected]
    if pending: return None
    if goal.target.get('deep_extraction'):
        from .extraction_development import development_method
        return development_method(goal, sources, facts)
    listing = await rt.game.invoke('home/bills', {'action': 'list', 'dryRun': True})
    candidates, deficits = [], []
    for bench in listing.get('benches', []):
        for bill in bench.get('bills', []):
            config = bill.get('config', {})
            covers = config.get('repeatMode') == 'Forever' or (config.get('repeatMode') == 'TargetCount' and config.get('targetCount', 0) >= target)
            if (covers and not bill.get('suspended') and not bill.get('finished')
                    and any(p.get('defName') == resource for p in bill.get('products', []))):
                goal.evidence['work_types'] = bill.get('workTypes', [])
                goal.evidence['existing_bill'] = {'bench': bench['thingId'], 'bill': bill.get('billId')}
                return None
        recipes = await rt.game.invoke('home/bills', {'action': 'recipes', 'bench': bench['thingId'], 'dryRun': True})
        for recipe in recipes.get('recipes') or []:
            if not any(p.get('defName') == resource for p in recipe.get('products', [])): continue
            costs = ingredient_deficits(recipe, facts.get('resources', {}))
            deficits.append({'bench': bench['thingId'], 'recipe': recipe['defName'], 'ingredients': costs,
                             'researchBlocked': recipe.get('availableNow') is False})
            if recipe.get('availableNow') is True and recipe.get('availableOnNow') is True:
                ingredients_available=all(any(choice['deficit']==0 for choice in slot) for slot in costs)
                candidates.append((not ingredients_available, bench['thingId'], recipe['defName'], recipe.get('workTypes', [])))
    goal.evidence['production_deficits'] = deficits
    if not candidates: raise SkillBlocked('No available native production recipe and workbench for ' + resource)
    _, bench, recipe, work_types = sorted(candidates, key=lambda c: c[:3])[0]
    goal.evidence['work_types'] = work_types
    method = 'resource-' + fingerprint({'resource':resource,'target':target,'bench':bench,'recipe':recipe})[:12]
    if goal.method_seen(method):
        raise SkillBlocked('Previously issued production bill no longer covers this target; explicitly renew the resource goal to replace it')
    return method, [native('home/bills', action='add', bench=bench, recipe=recipe,
        repeatMode='TargetCount', targetCount=target, unpauseWhenYouHave=max(0, target-1),
        pauseWhenSatisfied='on', watch=False)]


async def refresh_resource_prerequisite(rt, goal_id, facts):
    """Reconsider native availability without writing or replacing player-altered bills."""
    from .colony_skills import SkillBlocked
    goal=rt.current_plan.colony_goals[goal_id]
    if goal.cancelled or goal.status != 'blocked' or not goal.reason.startswith((
            'No available native production recipe', 'Resource stock is unavailable',
            'Native resource sources unavailable', 'Material storage unavailable', 'Extraction development')):
        return False
    try:
        await resource_method(rt, goal_id, facts)
    except SkillBlocked as error:
        goal.reason=str(error)
        return False
    goal.status, goal.reason='active', ''
    return True
