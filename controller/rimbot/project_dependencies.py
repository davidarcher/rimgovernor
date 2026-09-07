"""Prerequisites extend the existing project/work lifecycle; no second task queue."""
from .contracts import Action


def validate_graph(projects, changes):
    graph={p['project_id']:p.get('after_projects',[]) for p in projects}
    graph.update(changes)
    for key,dependencies in graph.items():
        if len(dependencies)!=len(set(dependencies)):raise ValueError('Duplicate project prerequisite')
        if any(d not in graph for d in dependencies):raise ValueError('Prerequisite must reference a known project')
    visiting=set();visited=set()
    def visit(key):
        if key in visiting:raise ValueError('Project prerequisites form a cycle')
        if key in visited:return
        visiting.add(key)
        for child in graph[key]:visit(child)
        visiting.remove(key);visited.add(key)
    for key in graph:visit(key)


def order_projects(projects):
    """Respect caller priority order while running prerequisites before dependants."""
    by_id={p['project_id']:p for p in projects};result=[];visited=set();visiting=set()
    def visit(project):
        key=project['project_id']
        if key in visiting:raise ValueError('Project prerequisites form a cycle')
        if key in visited:return
        visiting.add(key)
        for dependency in project.get('after_projects',[]):
            if dependency in by_id:visit(by_id[dependency])
        visiting.remove(key);visited.add(key);result.append(project)
    for project in projects:visit(project)
    return result


async def check_dependencies(rt, project):
    projects={p['project_id']:p for p in rt.memory.get('projects',[])}
    work={w['id']:w for w in rt.memory.get('work',[])}
    blockers=[]
    for identity in project.get('after_projects',[]):
        prerequisite=projects.get(identity)
        if prerequisite is None or prerequisite.get('cancelled_by_player'):
            blockers.append(f'Prerequisite {identity} is missing or cancelled; revise the dependency.')
            continue
        ids=prerequisite.get('work_ids',[])
        if prerequisite.get('status') not in ('orders_verified','retired') or not ids or any(i not in work or work[i]['status']!='complete' for i in ids):
            blockers.append(f"Waiting for verified orders: {prerequisite['outcome']}")
            continue
        # Previously complete buildings/settings may have been removed or changed.
        for identity in ids:
            row=work[identity]
            try:
                verified=await rt.action_complete(Action.model_validate(row.get('action',{})),row.get('native_result'))
            except (ValueError,RuntimeError) as error:
                blockers.append('Prerequisite observation unavailable: '+str(error));break
            if not verified:
                blockers.append(f"Prerequisite no longer verified: {prerequisite['outcome']}");break
    previous=project.get('dependency_blockers',[])
    project['dependency_blockers']=blockers
    if blockers!=previous:
        rt.note('project_dependency','; '.join(blockers) if blockers else 'Prerequisites verified; project can continue',project_id=project['project_id'])
        if not blockers:project.pop('execution_review',None)
        rt.persist()
    deadline=project.get('deadline_tick')
    overdue=deadline is not None and (rt.last_tick or 0)>deadline
    if overdue and not project.get('deadline_overdue'):
        rt.memory['admin_requested']='Project missed its game-time target: '+project['outcome']
        rt.note('project_deadline',rt.memory['admin_requested'],project_id=project['project_id'])
    project['deadline_overdue']=overdue
    if blockers:rt.persist()
    return not blockers
