"""Shared commitment-time reservations for player and autopilot construction."""
from .colony_plan import Buildings, RoomShell, NativeOperation
from .hands import room_placements


class ResourceShortage(ValueError):
    """Known pre-write stock shortage; unknown costs and policies remain refusals."""
    def __init__(self, resource, required, available):
        self.evidence = {'resource':resource,'required':required,'available':available}
        super().__init__('Current resources no longer cover reservations: '+resource)


async def validate_allocations(spec, current, game):
    previous = {s.id: s for s in current.spec.steps}
    slots_by_step = {}
    spendable, requested = {}, {}
    held = {}
    retained = {s.id for s in spec.steps}
    for identity, slots in current.control.get('costs', {}).items():
        if identity not in retained: continue
        progress = current.progress.get(identity)
        if progress is None or progress.state in ('complete', 'cancelled', 'blocked'): continue
        for slot, costs in slots.items():
            if progress.issued.get(slot, {}).get('confirmed'): continue
            for resource, count in costs.items(): held[resource] = held.get(resource, 0) + count
    policy = current.control.get('resource_policy', {})
    for step in spec.steps:
        if step.id in previous and step.signature() == previous[step.id].signature(): continue
        if step.source == 'LLM_ADVISOR':
            raise ValueError('Advisory recommendations cannot commit game orders')
        action = step.action
        placements = room_placements(action) if isinstance(action, RoomShell) else action.placements if isinstance(action, Buildings) else []
        slots = {}
        for index, placement in enumerate(placements):
            choices = placement.materials or [None]
            accepted = None
            for material in choices:
                args = dict(defName=placement.def_name, x=placement.x, z=placement.z,
                            rotation=placement.rotation, dryRun=True)
                if material: args['stuff'] = material
                preview = await game.invoke('home/place_building', args, allow_write=False)
                if preview.get('canPlace') is True:
                    accepted = preview
                    # Reserve the material that was actually costed. A fallback
                    # needs a new validation, not a different execution budget.
                    if material: placement.materials = [material]
                    break
            if accepted is None: raise ValueError('No legal costed placement: '+placement.def_name)
            materials = accepted.get('materials')
            if materials is None or materials.get('unreadable') or 'costList' not in accepted:
                raise ValueError('Native construction costs are unavailable; no allocation accepted')
            costs = {row['defName']: row['count'] for row in accepted['costList']}
            slots[str(index)] = costs
            for row in materials.get('rows', []):
                resource = row['defName']
                if row.get('available') is None: raise ValueError('Spendable '+resource+' is unknown')
                spendable[resource] = min(spendable.get(resource, row['available']), row['available'])
            for resource, count in costs.items():
                rules = policy.get(resource, {})
                if rules.get('spending') == 'stop' or (rules.get('spending') == 'defense_only' and step.purpose != 'defense'):
                    raise ValueError('Player resource policy prevents '+resource+' spending for '+step.purpose)
                requested[resource] = requested.get(resource, 0) + count
        if slots: slots_by_step[step.id] = slots
    deficits = {resource: amount + held.get(resource, 0) + policy.get(resource, {}).get('reserve', 0) - spendable.get(resource, 0)
                for resource, amount in requested.items()
                if amount + held.get(resource, 0) + policy.get(resource, {}).get('reserve', 0) > spendable.get(resource, 0)}
    if deficits: raise ValueError('Resource reservation rejected: '+str(deficits))
    return slots_by_step


def execution_reservations(plan, step_id):
    """Budget ready work in dispatch order after native consumption changes stock.

    Admission still reserves every accepted project. At dispatch, lower-priority
    or dependency-gated work yields to ready prerequisites. Each selected project
    retains its full unissued batch budget; uncertain writes retain their costs
    regardless of scheduling priority until observation resolves them.
    """
    ready = {step.id for step in plan.ready()}
    selected = {step_id}
    for step in sorted(plan.spec.steps, key=lambda candidate: -candidate.priority):
        if step.id == step_id:
            break
        if step.id in ready:
            selected.add(step.id)
    held = {}
    for identity, slots in plan.control.get('costs', {}).items():
        progress = plan.progress.get(identity)
        if progress is None:
            continue
        for slot, costs in slots.items():
            issued = progress.issued.get(slot)
            if issued and issued.get('confirmed') is True:
                continue
            uncertain = issued is not None and issued.get('confirmed') is not True
            if identity not in selected and not uncertain:
                continue
            for resource, count in costs.items():
                held[resource] = held.get(resource, 0) + count
    return held


def validate_execution_costs(plan, progress, slot, preview):
    """Recheck policy and accepted reservations against the current native stock."""
    policies = plan.control.get('resource_policy', {})
    if not policies and not plan.control.get('costs'): return
    step = next(s for s in plan.spec.steps if plan.progress[s.id] is progress)
    expected = plan.control.get('costs', {}).get(step.id, {}).get(slot)
    if expected is None and not policies: return  # Legacy plans retain native checks.
    if 'costList' not in preview or (preview.get('materials') or {}).get('unreadable'):
        raise ValueError('Cannot verify current construction resource costs')
    costs = {r['defName']:r['count'] for r in preview['costList']}
    if expected is not None and costs != expected:
        raise ValueError('Construction costs changed; revalidate the resource reservation')
    held = execution_reservations(plan, step.id)
    available = {r['defName']:r.get('available') for r in preview.get('materials',{}).get('rows',[])}
    for resource, count in costs.items():
        policy = policies.get(resource,{})
        if policy.get('spending') == 'stop' or (policy.get('spending') == 'defense_only' and step.purpose != 'defense'):
            raise ValueError('Player resource policy prevents '+resource+' spending for '+step.purpose)
        required = max(count,held.get(resource,0)) + policy.get('reserve',0)
        if available.get(resource) is None:
            raise ValueError('Current resource availability is unknown: '+resource)
        if available[resource] < required:
            raise ResourceShortage(resource,required,available[resource])
