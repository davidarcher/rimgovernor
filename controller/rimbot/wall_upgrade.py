"""Bounded stone replacement using shared construction, budgets and native guards."""
from .colony_plan import Buildings, RoomShell, NativeOperation
from .construction_ownership import owned_buildings
from .native_forecasts import finite
from .strategic_state import fingerprint


def evidence(facts, plan):
    if plan is None:
        return []
    if not any(s.source == 'AUTOPILOT' and isinstance(s.action, (Buildings, RoomShell))
               and plan.progress[s.id].state == 'complete' for s in plan.spec.steps):
        return []
    owned = owned_buildings(plan, facts)
    raw = facts.get('upkeep') or {}
    structures = raw.get('structures')
    if owned is None or not isinstance(structures, list) or 'structures' in raw.get('errors', {}):
        return None
    walls = [r for r in structures if r.get('id') in owned and r.get('defName') == 'Wall']
    if any(finite(r.get('flammability')) is None for r in walls):
        return None
    return sorted([dict(r, count=1) for r in walls if r['flammability'] > 0], key=lambda r: r['id'])


def reference(row):
    return dict(step=row['step'], slot=int(row['slot']))


def resolve(ref, rows, *, present=True):
    matches = [r for r in rows.values() if r['step'] == ref.step and int(r['slot']) == ref.slot]
    if len(matches) != 1 or present and matches[0]['present'] is not True:
        raise ValueError('Exact confirmed autonomous construction identity is unavailable')
    return matches[0]


async def arguments(rt, step):
    guard = step.action.wall_guard
    goal = rt.current_plan.colony_goals.get(step.goal_id)
    if step.source != 'AUTOPILOT' or goal is None or goal.source != 'AUTOPILOT' or goal.cancelled or guard is None:
        raise ValueError('Wall demolition requires an active autonomous construction bundle')
    facts = await rt.game.query('home/colony_facts', planning=True)
    rows = owned_buildings(rt.current_plan, facts, include_missing=True)
    if rows is None:
        raise ValueError('Current native construction identities are unavailable')
    original = resolve(guard.original, rows, present=guard.permanent is None)
    target = resolve(guard.target, rows)
    backups = [resolve(ref, rows, present=guard.permanent is None) for ref in guard.backups]
    permanent = resolve(guard.permanent, rows) if guard.permanent else None
    if original['definition'] != 'Wall' or (original['x'], original['z']) != (guard.x, guard.z):
        raise ValueError('Original wall geometry changed')
    return dict(action='remove', target=target['current'], original=original['current'],
        left=guard.left, right=guard.right, backup=';'.join(r['current'] for r in backups),
        permanent=permanent['current'] if permanent else '', x=guard.x, z=guard.z, nx=guard.nx, nz=guard.nz,
        **({'material': guard.material} if guard.material else {}))


async def validate_bundle(spec, current, game):
    """Only this proved dependency may reserve a wall site before demolition."""
    previous = {s.id: s for s in current.spec.steps}
    by_id = {s.id: s for s in spec.steps}
    def depends_on(step, ancestor, seen=None):
        seen = set() if seen is None else seen
        if step.id in seen:
            return False
        seen.add(step.id)
        return any(d.when == 'complete' and (d.step == ancestor or d.step in by_id
                   and depends_on(by_id[d.step], ancestor, seen)) for d in step.after)
    for removal in spec.steps:
        if not isinstance(removal.action, NativeOperation) or removal.action.wall_guard is None \
                or removal.id in previous and removal.signature() == previous[removal.id].signature():
            continue
        guard = removal.action.wall_guard
        followers = [s for s in spec.steps if isinstance(s.action, Buildings) and s.action.replacement_of == guard.original]
        if removal.source != 'AUTOPILOT' or len(followers) != 1 or followers[0].goal_id != removal.goal_id:
            raise ValueError('Demolition must belong to one complete autonomous replacement bundle')
        permanent = followers[0]
        if guard.permanent is None:
            if guard.target != guard.original or not depends_on(permanent, removal.id):
                raise ValueError('Original demolition must precede its permanent replacement')
        elif guard.permanent.step != permanent.id or guard.permanent.slot != 0 \
                or guard.target not in guard.backups or not depends_on(removal, permanent.id):
            raise ValueError('Backup removal requires completed permanent construction')
    replacements = [s for s in spec.steps if isinstance(s.action, Buildings) and s.action.replacement_of
                    and (s.id not in previous or s.signature() != previous[s.id].signature())]
    if not replacements:
        return set()
    facts = await game.query('home/colony_facts', planning=True)
    owned = owned_buildings(current, facts)
    if owned is None:
        raise ValueError('Wall replacement requires current owned construction evidence')
    allowed = set()
    for step in replacements:
        action = step.action
        original = resolve(action.replacement_of, owned)
        if step.source != 'AUTOPILOT' or len(action.placements) != 1:
            raise ValueError('A replacement batch admits one autonomous permanent wall')
        p = action.placements[0]
        if p.def_name != 'Wall' or (p.x, p.z) != (original['x'], original['z']) or len(p.materials) != 1:
            raise ValueError('Replacement definition, material or original geometry is invalid')
        removals = [by_id[d.step] for d in step.after if d.step in by_id and d.when == 'complete'
            and isinstance(by_id[d.step].action, NativeOperation) and by_id[d.step].action.wall_guard]
        if len(removals) != 1:
            raise ValueError('Permanent wall needs one guarded native demolition dependency')
        removal, guard = removals[0], removals[0].action.wall_guard
        if removal.source != 'AUTOPILOT' or removal.goal_id != step.goal_id or guard.permanent is not None \
                or guard.original != action.replacement_of or guard.target != guard.original:
            raise ValueError('Replacement dependency does not own the original wall')
        if guard.material is not None and guard.material != p.materials[0]:
            raise ValueError('Guarded corner material differs from permanent construction')
        sites = await game.invoke('home/wall_upgrade_sites', dict(target=original['current']), allow_write=False)
        site = next((r for r in sites.get('sites', []) if all(r.get(k) == getattr(guard, k)
                     for k in ('x', 'z', 'nx', 'nz', 'left', 'right'))), None)
        if sites.get('success') is not True or site is None \
                or p.materials[0] not in {r['defName'] for r in sites.get('materials', [])}:
            raise ValueError('Native replacement geometry or stone material is unavailable')
        for ref, cell in zip(guard.backups, site['backupCells'], strict=True):
            backup = by_id.get(ref.step)
            if (backup is None or backup.source != 'AUTOPILOT' or backup.goal_id != step.goal_id
                    or not isinstance(backup.action, Buildings) or ref.slot >= len(backup.action.placements)
                    or not any(d.step == backup.id and d.when == 'complete' for d in removal.after)):
                raise ValueError('Completed backup construction dependency is missing')
            bp = backup.action.placements[ref.slot]
            if bp.def_name != 'Wall' or (bp.x, bp.z) != (cell['x'], cell['z']) or bp.materials != p.materials:
                raise ValueError('Backup construction differs from the native enclosure')
        allowed.add(step.id)
    return allowed


def reconcile(rt, facts):
    from .colony_plan import Failure
    raw = facts.get('upkeep') or {}
    rows = raw.get('wallRemoval')
    if raw.get('tick') != facts.get('tick') or raw.get('version') != 1 \
            or not isinstance(rows, list) or 'wallRemoval' in raw.get('errors', {}):
        return
    for step in rt.current_plan.spec.steps:
        if getattr(step.action, 'completion', None) != 'wall_removed':
            continue
        progress = rt.current_plan.progress[step.id]
        receipt = progress.issued.get('0', {})
        if progress.state not in ('waiting', 'blocked') or not receipt.get('confirmed'):
            continue
        changed = receipt.get('load_token') != rt.context_token or receipt.get('player_direction') != rt.current_plan.control.get('player_direction', 0)
        matches = [r for r in rows if r.get('id') == receipt.get('wall_removal_id')]
        row = matches[0] if len(matches) == 1 else None
        if row and row.get('retired') is True and row.get('targetPresent') is True \
                and row.get('designated') is False and row.get('complete') is False \
                and row.get('playerOwned') is False and row.get('target') == receipt.get('wall_target'):
            retire_batch(rt.current_plan, step, row)
            continue
        if changed or row and (row.get('blocker') or row.get('playerOwned') or row.get('target') != receipt.get('wall_target')):
            progress.state = 'blocked'
            progress.failure = Failure(code='wall_removal_invalidated', detail='Wall demolition ownership or safety changed; inspection required')
        elif row and row.get('complete') is True and finite(row.get('completedTick')) is not None \
                and receipt.get('issued_tick', 0) <= row['completedTick'] <= facts['tick']:
            progress.state = 'complete'
            receipt['postcondition'] = row


def retire_batch(plan, removal, evidence):
    """Native cancellation retires history, never unobserved construction writes."""
    descendants = {removal.id}
    for _ in plan.spec.steps:
        added = {s.id for s in plan.spec.steps if s.goal_id == removal.goal_id
                 and any(d.step in descendants for d in s.after)} - descendants
        if not added:
            break
        descendants.update(added)
    for step in plan.spec.steps:
        progress = plan.progress[step.id]
        if step.id not in descendants or progress.state in ('complete', 'cancelled'):
            continue
        if step.id != removal.id and progress.issued:
            continue
        plan.cancel(step.id)
    plan.progress[removal.id].issued['0']['retirement'] = evidence


async def release_pending(rt, *, changed_only=False):
    plan = getattr(rt, 'current_plan', None)
    if plan is None:
        return
    pending = [(s, plan.progress[s.id]) for s in plan.spec.steps
               if getattr(s.action, 'completion', None) == 'wall_removed'
               and plan.progress[s.id].issued and plan.progress[s.id].state != 'complete']
    def batch_invalid(step):
        if plan.progress[step.id].state == 'blocked':
            return True
        goal = plan.colony_goals.get(step.goal_id)
        if goal is None or goal.cancelled or goal.status != 'active':
            return True
        guard = step.action.wall_guard
        if guard.permanent is not None:
            return plan.progress[guard.permanent.step].state != 'complete'
        successors = [s for s in plan.spec.steps if isinstance(s.action, Buildings)
                      and s.action.replacement_of == guard.original and s.goal_id == step.goal_id]
        return len(successors) != 1 or plan.progress[successors[0].id].state not in ('pending', 'executing', 'waiting')
    if changed_only:
        pending = [(s, p) for s, p in pending if batch_invalid(s) or any(r.get('load_token') != rt.context_token
            or r.get('player_direction') != plan.control.get('player_direction', 0) for r in p.issued.values())]
    if not pending:
        return
    signature = fingerprint(dict(context=rt.context_token, direction=plan.control.get('player_direction', 0),
                                 receipts=[p.issued for _, p in pending]))
    if plan.control.get('wall_release_signature') == signature:
        return
    result = await rt.game.invoke('home/upkeep_wall', dict(action='release', dryRun=False), allow_write=True)
    if result.get('success') is not True:
        raise ValueError('Pending wall demolition could not be invalidated')
    plan.control['wall_release_signature'] = signature


def removal_record(plan, identity):
    """Read the same immutable action before or after shared-plan archival."""
    from .colony_plan import PlanStep, StepProgress
    current = next((s for s in plan.spec.steps if s.id == identity), None)
    if current is not None:
        return current, plan.progress[identity]
    retired = plan.control.get('retired_steps', {}).get(identity)
    if retired is not None:
        return PlanStep.model_validate(retired), plan.progress[identity]
    archived = plan._archive_read(identity) if plan._archive_read is not None else None
    if not archived:
        raise ValueError('Archived wall demolition evidence is unavailable')
    return PlanStep.model_validate(archived['step']), StepProgress.model_validate(archived['progress'])


async def project_handoffs(plan, game):
    """Delegate only slots whose exact native demolition has been observed."""
    from types import SimpleNamespace
    identities = {s.id for s in plan.spec.steps if getattr(s.action, 'wall_guard', None) is not None
                and plan.progress[s.id].issued.get('0', {}).get('confirmed') is True}
    identities.update(plan.control.get('wall_handoff_removals', []))
    removals = [removal_record(plan, identity) for identity in sorted(identities)]
    if not removals:
        plan.control.pop('wall_handoffs', None)
        return {}
    identity = await game.query('home/colony_identity')
    facts = await game.query('home/colony_facts', planning=True)
    after = await game.query('home/colony_identity')
    fields = ('colonyId', 'mapId', 'loadToken')
    if any(identity.get(k) is None or identity.get(k) != after.get(k) for k in fields):
        raise ValueError('Colony changed while observing construction handoff')
    token = f'{identity["colonyId"]}:{identity["mapId"]}:{identity["loadToken"]}'
    reconcile(SimpleNamespace(current_plan=plan, context_token=token), facts)
    raw = facts.get('upkeep') or {}
    ledger = raw.get('wallRemoval')
    owned = owned_buildings(plan, facts, include_missing=True)
    if owned is None or not isinstance(ledger, list) or 'wallRemoval' in raw.get('errors', {}):
        raise ValueError('Native construction handoff evidence unavailable')
    handoffs = {}
    for step, progress in removals:
        if getattr(step.action, 'wall_guard', None) is None:
            raise ValueError('Construction handoff does not reference guarded demolition')
        receipt = progress.issued['0']
        matches = [r for r in ledger if r.get('id') == receipt.get('wall_removal_id')]
        if progress.state != 'complete' or len(matches) != 1 or matches[0].get('complete') is not True \
                or matches[0].get('blocker') or matches[0].get('playerOwned') or matches[0].get('target') != receipt.get('wall_target'):
            continue
        guard = step.action.wall_guard
        source = resolve(guard.target, owned, present=False)
        if source['current'] != receipt['wall_target']:
            raise ValueError('Retired construction slot does not match native demolition')
        destination = guard.permanent
        if destination is None:
            successors = [s for s in plan.spec.steps if isinstance(s.action, Buildings)
                and s.action.replacement_of == guard.original and (any(d.step == step.id and d.when == 'complete' for d in s.after)
                    or step.id in plan.control.get('wall_handoff_removals', []))]
            if len(successors) != 1:
                raise ValueError('Native demolition has no unique retained replacement')
            from .colony_plan import ConstructionRef
            destination = ConstructionRef(step=successors[0].id, slot=0)
        successors = [r for r in owned.values() if r['step'] == destination.step and int(r['slot']) == destination.slot]
        successor = successors[0] if len(successors) == 1 else None
        state = plan.progress[destination.step].state
        status = ('complete' if successor and successor['present'] else
                  'pending' if state in ('pending', 'executing', 'waiting') else 'missing')
        handoffs[f'{guard.target.step}:{guard.target.slot}'] = dict(source_step=guard.target.step,
            definition=source['definition'], x=source['x'], z=source['z'], stuff=source['stuff'],
            removal=step.id, signature=step.signature(), destination=destination.model_dump(), status=status,
            thing=successor['current'] if successor and successor['present'] else None)
    plan.control['wall_handoffs'] = handoffs
    plan.control['wall_handoff_removals'] = sorted({h['removal'] for h in handoffs.values()}
        | set(plan.control.get('wall_handoff_removals', [])))
    return handoffs


async def method(rt, facts):
    from .colony_skills import SkillBlocked
    from .development import placement
    from .production_policy import resource_method
    goal_id = 'MaintainStoneShell'
    plan = rt.current_plan
    goal = plan.colony_goals[goal_id]
    state = plan.control['upkeep'][goal_id]
    goal.evidence.pop('waiting_for_stone_blocks', None)
    if not state['known']:
        raise SkillBlocked('Owned wall condition is unavailable')
    owned = owned_buildings(plan, facts)
    failures = []
    candidates = []
    for wall in state['targets']:
        key = 'wall-' + fingerprint(wall['id'])[:12]
        if goal.method_seen(key):
            if len(failures) < 8:
                failures.append(wall['id'] + ': previously admitted replacement is preserved; no duplicate demolition')
            continue
        candidates.append((wall, key))
        if len(candidates) == 8:
            break
    for wall, key in candidates:
        sites = await rt.game.invoke('home/wall_upgrade_sites', dict(target=wall['id']), allow_write=False)
        if sites.get('success') is not True or not sites.get('sites'):
            failures.append(wall['id'] + ': no empty supported wall backup site')
            continue
        site = sites['sites'][0]
        rows = [r for r in sites.get('materials', []) if r['defName'] in facts.get('policyResources', {})
                and plan.control.get('resource_policy', {}).get(r['defName'], {}).get('spending', 'normal') == 'normal']
        rows.sort(key=lambda r: (-facts.get('resources', {}).get(r['defName'], 0), r['defName']))
        if not rows:
            raise SkillBlocked('No permitted observed native stone material')
        material = rows[0]
        resource = material['defName']
        goal.evidence.setdefault('construction_costs', {}).setdefault('Wall', {})[resource] = material['costs']
        needed = material['costs'].get(resource)
        if type(needed) is not int or needed <= 0 or set(material['costs']) != {resource}:
            raise SkillBlocked('Native wall costs need unsupported mixed-material production')
        backup_count = len(site['backupCells'])
        if backup_count not in (0, 3):
            raise SkillBlocked('Native wall backup geometry is outside the bounded contract')
        quantity = (backup_count + 1) * needed
        if facts.get('resources', {}).get(resource, 0) < quantity:
            goal.target = dict(resource=resource, quantity=quantity)
            try:
                result = await resource_method(rt, goal_id, facts)
            except SkillBlocked as error:
                if str(error).startswith('No available native production recipe and workbench'):
                    if goal.method_seen('stonecutter'):
                        raise SkillBlocked('Owned stonecutter is unavailable; preserve uncertain or changed construction')
                    avoid = {(site['x'] - site['nx'], site['z'] - site['nz']), (site['x'], site['z'])}
                    avoid.update((c['x'], c['z']) for c in site['backupCells'])
                    if site['nx'] and site['nz']:
                        avoid.update(((site['x'] + site['nx'], site['z']), (site['x'], site['z'] + site['nz'])))
                    try:
                        bench = await placement(rt, facts, 'TableStonecutter', indoors=True, goal=goal,
                                                rotations='all', avoid=avoid)
                    except SkillBlocked as placement_error:
                        if not str(placement_error).startswith('No safe observed placement'):
                            raise
                        bench = await placement(rt, facts, 'TableStonecutter', indoors=False, goal=goal,
                                                rotations='all', avoid=avoid)
                    return 'stonecutter', [bench]
                raise
            if result is None:
                goal.evidence['waiting_for_stone_blocks'] = True
            return result
        step_id = lambda i: f'{goal_id}-{goal.attempts}-{key}-{i}'[:64]
        original = reference(owned[wall['id']])
        backups = [dict(step=step_id(0), slot=i) for i in range(backup_count)]
        guard = dict(original=original, target=original, backups=backups,
                     **{k: site[k] for k in ('x', 'z', 'nx', 'nz', 'left', 'right')})
        if not backup_count: guard['material'] = resource
        def remove(value):
            return dict(kind='native_operation', tool='home/upkeep_wall', arguments=dict(action='remove'),
                        completion='wall_removed', wall_guard=value)
        actions = ([dict(kind='place_buildings', placements=[dict(def_name='Wall', materials=[resource], **c) for c in site['backupCells']])]
                   if backup_count else [])
        actions.extend([remove(guard), dict(kind='place_buildings', replacement_of=original,
                placements=[dict(def_name='Wall', x=site['x'], z=site['z'], materials=[resource])])])
        for ref in backups:
            actions.append(remove(dict(guard, target=ref, permanent=dict(step=step_id(2), slot=0))))
        goal.evidence['wall_upgrade'] = dict(original=wall['id'], material=resource, reserved_blocks=quantity, site=site)
        return key, actions
    raise SkillBlocked('No bounded safe wall replacement: ' + '; '.join(failures))
