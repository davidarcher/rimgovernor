"""Native dry-run validation of new construction intent before commitment."""
from .colony_plan import Buildings, RoomShell
from .hands import room_placements


class ConstructionRefusal(ValueError):
    def __init__(self, step, placement, evidence):
        self.evidence={'step_id':step.id,'definition':placement.def_name,
            'cell':{'x':placement.x,'z':placement.z},'native':evidence}
        super().__init__(f'Construction step {step.id}: {placement.def_name} at '
            f'({placement.x}, {placement.z}) failed native preflight. '
            'Inspect native evidence and correct the plan; no plan change or construction was issued.')


async def preflight_construction(spec, current, game):
    previous={step.id:step for step in current.spec.steps}
    checked={}
    for step in spec.steps:
        old=previous.get(step.id)
        if old and old.signature()==step.signature():
            continue  # Existing work is reconciled by Hands, not rejected on re-planning.
        action=step.action
        placements=room_placements(action) if isinstance(action,RoomShell) else (
            action.placements if isinstance(action,Buildings) else [])
        for placement in placements:
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
                if result.get('success') is not False and (
                        result.get('canPlace') is True or (step.after and 'canPlace' in result)):
                    accepted=True;break
            if not accepted:
                raise ConstructionRefusal(step,placement,evidence)
