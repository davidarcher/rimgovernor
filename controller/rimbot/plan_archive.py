"""Durable completed-action receipts outside the frequently rewritten live plan."""


def bind_archive(plan, store, colony):
    if plan._archive_contains is None and plan.control.get('archived_action_count',0):
        count=store.db.execute('SELECT COUNT(*) FROM retired_actions WHERE colony=?',(colony,)).fetchone()[0]
        if count<plan.control['archived_action_count']:
            raise ValueError('Retired action archive is missing records; preserve the paired controller database')
    plan._archive_contains=lambda identity:store.has_retired_action(colony,identity)
    plan._archive_read=lambda identity:store.retired_action(colony,identity)
    for identity,goal in plan.colony_goals.items():
        if goal._method_contains is None and goal.archived_methods:
            count=store.db.execute('SELECT COUNT(*) FROM retired_methods WHERE colony=? AND goal=? AND epoch=?',
                (colony,identity,goal.method_epoch)).fetchone()[0]
            if count<goal.archived_methods:
                raise ValueError('Goal method archive is missing records; preserve the paired controller database')
        goal._method_contains=lambda name, identity=identity, goal=goal:store.has_retired_method(colony,identity,goal.method_epoch,name)


def prepare_archive(plan):
    """Return a compact serialized plan and records; leave live state untouched."""
    snapshot=plan.model_dump()
    active={step.id for step in plan.spec.steps}
    pinned=set(plan.control.get('combat',{}).get('steps',[]))
    records={}
    for identity,step in plan.control.get('retired_steps',{}).items():
        if identity in pinned:continue
        progress=plan.progress.get(identity)
        if identity in active or progress is None or progress.state!='complete':
            raise ValueError('Retired action lacks an immutable completed outcome: '+identity)
        records[identity]={'step':step,'progress':progress.model_dump(),
                           'costs':plan.control.get('costs',{}).get(identity)}
    methods=[]
    for identity,goal in plan.colony_goals.items():
        for name,steps in goal.evidence.get('methods',{}).items():
            if not steps or not isinstance(steps,list):continue
            if not all(step in records or plan._archive_contains is not None and plan._archive_contains(step) for step in steps):continue
            methods.append({'goal':identity,'epoch':goal.method_epoch,'method':name,'record':{'steps':steps}})
            row=snapshot['colony_goals'][identity]
            row['evidence']['methods'].pop(name)
            row['archived_methods']+=1
    if not records:return snapshot,records,methods
    control=snapshot['control']
    for identity in records:control['retired_steps'].pop(identity)
    if not control['retired_steps']:control.pop('retired_steps')
    control['archived_action_count']=control.get('archived_action_count',0)+len(records)
    for identity in records:
        snapshot['progress'].pop(identity)
        control.get('costs',{}).pop(identity,None)
    for goal in snapshot['colony_goals'].values():
        goal['steps']=[identity for identity in goal['steps'] if identity not in records]
    return snapshot,records,methods


def finish_archive(plan, snapshot, records, methods=()):
    """Compact live state only after the database transaction has committed."""
    for entry in methods:
        goal=plan.colony_goals[entry['goal']]
        goal.evidence['methods'].pop(entry['method'])
        goal.archived_methods=snapshot['colony_goals'][entry['goal']]['archived_methods']
    if not records:return
    for identity in records:plan.control['retired_steps'].pop(identity)
    if not plan.control['retired_steps']:plan.control.pop('retired_steps')
    plan.control['archived_action_count']=snapshot['control']['archived_action_count']
    for identity in records:
        plan.progress.pop(identity)
        plan.control.get('costs',{}).pop(identity,None)
    for identity,goal in plan.colony_goals.items():
        goal.steps=snapshot['colony_goals'][identity]['steps']
