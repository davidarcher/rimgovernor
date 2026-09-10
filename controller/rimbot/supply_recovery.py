"""Observe obsolete starter-stock allow requests without replaying a write."""
from math import ceil, hypot


async def recover_starting_supplies(rt, step_id, *, token, direction):
    async with rt.lock:
        plan=rt.current_plan
        step=next((step for step in plan.spec.steps if step.id==step_id),None)
        if step is None:return False
        if (step.source!='AUTOPILOT' or step.goal_id!='AllowStartingSupplies' or step.action.kind!='native_operation'
                or step.action.tool!='rimworld/apply_architect_designator'):return False
        progress=plan.progress[step_id];failure=progress.failure
        if progress.state!='blocked' or not failure or failure.code!='starting_supplies_unavailable':return False
        evidence=failure.evidence;revision=plan.revision
        goal=plan.colony_goals.get(step.goal_id)
        def valid():
            return (rt.current_plan is plan and plan.revision==revision and rt.mode=='automate'
                and rt.context_token==token==evidence.get('load_token') and rt.chat_revision==direction
                and plan.control.get('player_direction',0)==evidence.get('player_direction')
                and step.signature()==evidence.get('signature') and goal is not None and not goal.cancelled)
        if not valid():return False
        status=await rt.game.query('home/status',colonists=False,threats=False)
        tick=status.get('time',{}).get('ticksGame')
        if status.get('time',{}).get('paused') is not True or type(tick) is not int or tick<evidence['tick']:return False
        args=step.action.arguments
        # Aggregate the surrounding cells as a conservative superset. A truncated
        # position list cannot turn remaining forbidden stock into a false absence.
        radius=max(1,ceil(hypot(args.get('width',1)-1,args.get('height',1)-1)))
        observed=await rt.game.query('home/list_things',x=args['x'],z=args['z'],radius=radius,
            includeHeld=False,ownership='all',excludeChunks=False)
        if (observed.get('success') is not True or type(observed.get('forbiddenTotal')) is not int
                or observed['forbiddenTotal']!=0 or type(observed.get('foggedTotal')) is not int
                or observed['foggedTotal']!=0):return False
        await rt.sync_identity()
        await rt.refresh_clock_events()
        if not valid():return False
        progress.recovery_history.append({'tick':tick,'load_token':token,'failure':failure.model_dump(),
            'outcome':'already_allowed','observed':observed})
        progress.issued={'0':{'confirmed':True,'native_outcome':'already_allowed','load_token':token,'observed_tick':tick}}
        progress.state,progress.failure='complete',None
        goal.status,goal.reason='active',''
        plan.revision+=1
        rt.note('starting_supplies_reconciled','Starter stock is no longer forbidden; no allow command was replayed.',step=step_id)
        rt.persist()
        return True
