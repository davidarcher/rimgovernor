"""Native dry-run validation of new construction intent before commitment."""
from .colony_plan import Buildings, RoomShell, Zone
from .spatial import room_placements, native_footprint, validate_geometry, projected_obstruction, room_entrance
from .shell_site import validate_shell_site, validate_shell_connectivity
from .placement_previews import PlacementPreviews


class ConstructionRefusal(ValueError):
    def __init__(self, step, placement, evidence):
        self.evidence={'step_id':step.id,'definition':placement.def_name,
            'cell':{'x':placement.x,'z':placement.z},'native':evidence}
        super().__init__(f'Construction step {step.id}: {placement.def_name} at '
            f'({placement.x}, {placement.z}) failed native preflight. '
            'Inspect native evidence and correct the plan; no plan change or construction was issued.')


async def preflight_construction(spec, current, game, *, refresh=False, deferred_wall_steps=frozenset()):
    previous={step.id:step for step in current.spec.steps}
    def spatial_signature(plan):
        return [(s.id, s.signature()) for s in plan.steps if isinstance(s.action, (Buildings, RoomShell, Zone))]
    if (not refresh and spatial_signature(spec) == spatial_signature(current.spec)
            and spec.reserved_walkways == current.spec.reserved_walkways):
        return
    validate_geometry(spec, current=current)
    checked={}
    footprints={}
    obstructions=set()
    costs={}
    stock={}
    # Cancelled intent cannot project unbuilt walls. Surviving native objects
    # remain part of the observed map used by the access audit.
    active=[s for s in spec.steps if not (s.id in previous and previous[s.id].signature()==s.signature()
        and current.progress.get(s.id) and current.progress[s.id].state=='cancelled')]
    has_shells=any(isinstance(s.action, RoomShell) for s in active)
    for step in active:
        old=previous.get(step.id)
        unchanged=old is not None and old.signature()==step.signature()
        action=step.action
        placements=room_placements(action) if isinstance(action,RoomShell) else (
            action.placements if isinstance(action,Buildings) else [])
        previews = PlacementPreviews(game, placements, checked)
        for index, placement in enumerate(placements):
            evidence={}
            accepted=False
            for stuff in placement.materials or [None]:
                key=(placement.def_name,placement.x,placement.z,placement.rotation,stuff)
                if key not in checked:
                    try:
                        checked[key]=await previews.get(placement, stuff)
                    except Exception as error:
                        checked[key]={'success':False,'error':str(error)[:1200]}
                result=checked[key]
                evidence={k:result[k] for k in ('success','error','canPlace','researchFinished',
                    'buildableByPlayer','madeFromStuff','rotations','materials') if k in result}
                if result.get('madeFromStuff') and stuff is None:
                    evidence['error']='Choose acceptable observed materials explicitly; do not rely on the native default.'
                    continue
                # Dependent work can need clearance first. Still require a resolved
                # definition; leave site readiness to execution after dependencies.
                relocation = any(dependency.step == candidate.id and candidate.action.kind == 'cancel_construction'
                    for dependency in step.after for candidate in spec.steps)
                confirmed = (relocation and unchanged and step.id in current.progress
                             and current.progress[step.id].issued.get(str(index),{}).get('confirmed') is True)
                if relocation and not confirmed:
                    from .shelter_handoff import safe_rotation
                    rows = result.get('rotations', [])
                    if len(rows) != 1 or not safe_rotation(rows[0]):
                        evidence['error'] = 'Relocation replacement must preserve all existing native objects.'
                        continue
                if (result.get('success') is not False or step.id in deferred_wall_steps) and (unchanged or
                        result.get('canPlace') is True or (step.after and not relocation and 'canPlace' in result)):
                    try:
                        footprints[(step.id, str(index))] = native_footprint(result, placement)
                        obstructions.update(projected_obstruction(result, placement))
                    except ValueError as error:
                        evidence['error'] = str(error)
                        continue
                    if isinstance(result.get('costList'),list):
                        for row in result['costList']:
                            budget=costs.setdefault(step.id,{})
                            budget[row['defName']]=budget.get(row['defName'],0)+row['count']
                    for row in (result.get('materials') or {}).get('rows',[]):
                        available=row.get('available')
                        if type(available) in (int,float):
                            stock[row['defName']]=min(stock.get(row['defName'],available),available)
                    accepted=True;break
            if not accepted:
                raise ConstructionRefusal(step,placement,evidence)
        if isinstance(action, RoomShell) and not unchanged:
            async def read(name, arguments):
                return await game.invoke(name, arguments, allow_write=False)
            await validate_shell_site(step.id, action, read)
    validate_geometry(spec, footprints, current=current)
    if has_shells:
        projected_spec=spec.model_copy(update={'steps':active})
        async def read(name, arguments):
            return await game.invoke(name, arguments, allow_write=False)
        for step in active:
            if isinstance(step.action, RoomShell):
                # Furniture or new walls can seal a room with no remaining work.
                await validate_shell_connectivity(step.id, step.action, read, projected_spec,
                                                  obstructions=obstructions)
    if footprints:
        from .shell_site import ShellSiteRefusal
        targets=set()
        for step in active:
            if isinstance(step.action,RoomShell):
                (x,z),(dx,dz)=room_entrance(step.action)
                targets.update(((x-dx,z-dz),(x+dx,z+dz)))
        encode=lambda cells:';'.join(f'{x},{z}' for x,z in sorted(cells))
        result=await game.invoke('home/spatial_access',dict(blockedCells=encode(obstructions),
            targetCells=encode(targets)),allow_write=False)
        if (result.get('success') is not True or type(result.get('accepted')) is not bool
                or type(result.get('pawnCount')) is not int or result['pawnCount']<1):
            raise ShellSiteRefusal('spatial-access','incomplete_pawn_access',
                'Current native pawn-specific access is unavailable',native=result)
        if result['accepted'] is not True:
            raise ShellSiteRefusal('spatial-access','projected_pawn_access',
                'Construction would remove currently accessible space or has no safe native route',native=result)
        return dict(costs=costs,stock=stock,access=result)
