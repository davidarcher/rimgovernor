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
    checked={}
    async def inspect(identity, ancestry):
        if identity in ancestry:
            return ['Project prerequisite cycle: '+' -> '.join((*ancestry,identity))]
        if identity in checked:return checked[identity]
        prerequisite=projects.get(identity)
        if prerequisite is None or prerequisite.get('cancelled_by_player'):
            reasons=[f'Prerequisite {identity} is missing or cancelled; revise the dependency.']
        elif prerequisite.get('admin_hold') or prerequisite.get('status')=='suspended':
            reasons=[f"Prerequisite is on hold: {prerequisite['outcome']}"]
        else:
            reasons=[]
            for parent in prerequisite.get('after_projects',[]):
                reasons.extend(await inspect(parent,(*ancestry,identity)))
            ids=prerequisite.get('work_ids',[])
            if not reasons:
                if prerequisite.get('status') not in ('orders_verified','retired') or not ids or any(i not in work or work[i]['status']!='complete' for i in ids):
                    reasons.append(f"Waiting for verified orders: {prerequisite['outcome']}")
                else:
                    for work_id in ids:
                        row=work[work_id]
                        try:
                            verified=await rt.action_complete(Action.model_validate(row.get('action',{})),row.get('native_result'))
                        except (ValueError,RuntimeError) as error:
                            reasons.append('Prerequisite observation unavailable: '+str(error));break
                        if not verified:
                            reasons.append(f"Prerequisite no longer verified: {prerequisite['outcome']}");break
        checked[identity]=list(dict.fromkeys(reasons))
        return checked[identity]
    blockers=[]
    for identity in project.get('after_projects',[]):
        blockers.extend(await inspect(identity,(project['project_id'],)))
    blockers=list(dict.fromkeys(blockers))
    project['dependency_checks']={'observed_tick':rt.last_tick,
        'projects':[{'project_id':identity,'verified':not reasons,'blockers':reasons} for identity,reasons in checked.items()]}
    previous=project.get('dependency_blockers',[])
    project['dependency_blockers']=blockers
    if blockers!=previous:
        rt.note('project_dependency','; '.join(blockers) if blockers else 'Prerequisites verified; project can continue',project_id=project['project_id'])
        if not blockers:project.pop('execution_review',None)
        rt.persist()
    deadline=project.get('deadline_tick')
    overdue=deadline is not None and (rt.last_tick or 0)>deadline
    if overdue and not project.get('deadline_overdue'):
        from .admin_requests import request_review
        reason='Project missed its game-time target: '+project['outcome']
        request_review(rt,project['project_id']+':deadline',reason)
        rt.note('project_deadline',reason,project_id=project['project_id'])
    project['deadline_overdue']=overdue
    if blockers:rt.persist()
    return not blockers
