"""Native dry-run validation of new construction intent before commitment."""
from .colony_plan import Buildings, RoomShell, Zone
from .spatial import room_placements, native_footprint, validate_geometry
from .shell_site import validate_shell_site, validate_shell_connectivity


class ConstructionRefusal(ValueError):
    def __init__(self, step, placement, evidence):
        self.evidence={'step_id':step.id,'definition':placement.def_name,
            'cell':{'x':placement.x,'z':placement.z},'native':evidence}
        super().__init__(f'Construction step {step.id}: {placement.def_name} at '
            f'({placement.x}, {placement.z}) failed native preflight. '
            'Inspect native evidence and correct the plan; no plan change or construction was issued.')


async def preflight_construction(spec, current, game):
    previous={step.id:step for step in current.spec.steps}
    def spatial_signature(plan):
        return [(s.id, s.signature()) for s in plan.steps if isinstance(s.action, (Buildings, RoomShell, Zone))]
    def shells(plan):
        return [(s.id, s.signature()) for s in plan.steps if isinstance(s.action, RoomShell)]
    shells_changed = shells(spec) != shells(current.spec)
    if (spatial_signature(spec) == spatial_signature(current.spec)
            and spec.reserved_walkways == current.spec.reserved_walkways):
        return
    validate_geometry(spec, current=current)
    checked={}
    footprints={}
    for step in spec.steps:
        old=previous.get(step.id)
        unchanged=old is not None and old.signature()==step.signature()
        action=step.action
        placements=room_placements(action) if isinstance(action,RoomShell) else (
            action.placements if isinstance(action,Buildings) else [])
        for index, placement in enumerate(placements):
            evidence={}
            accepted=False
            for stuff in placement.materials or [None]:
                args=dict(defName=placement.def_name,x=placement.x,z=placement.z,
                          rotation=placement.rotation,dryRun=True)
                if stuff is not None:args['stuff']=stuff
                key=(placement.def_name,placement.x,placement.z,placement.rotation,stuff)
                if key not in checked:
                    try:
                        checked[key]=await game.invoke('home/place_building',args,allow_write=False)
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
                if relocation:
                    from .shelter_handoff import safe_rotation
                    rows = result.get('rotations', [])
                    if len(rows) != 1 or not safe_rotation(rows[0]):
                        evidence['error'] = 'Relocation replacement must preserve all existing native objects.'
                        continue
                if result.get('success') is not False and (unchanged or
                        result.get('canPlace') is True or (step.after and not relocation and 'canPlace' in result)):
                    try:
                        footprints[(step.id, str(index))] = native_footprint(result, placement)
                    except ValueError as error:
                        evidence['error'] = str(error)
                        continue
                    accepted=True;break
            if not accepted:
                raise ConstructionRefusal(step,placement,evidence)
        if isinstance(action, RoomShell) and not unchanged:
            async def read(name, arguments):
                return await game.invoke(name, arguments, allow_write=False)
            await validate_shell_site(step.id, action, read)
    validate_geometry(spec, footprints, current=current)
    if shells_changed:
        async def read(name, arguments):
            return await game.invoke(name, arguments, allow_write=False)
        for step in spec.steps:
            if isinstance(step.action, RoomShell):
                # New walls can seal an existing room that has no work left to
                # redispatch. Validate both sides of that spatial relationship.
                await validate_shell_connectivity(step.id, step.action, read, spec)
