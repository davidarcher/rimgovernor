"""Continuous furnishing aisles in exact observed room shapes."""
from collections import deque


def neighbors(point, cells):
    x,z=point
    return sorted(p for p in ((x-1,z),(x+1,z),(x,z-1),(x,z+1)) if p in cells)


def articulation_cells(cells, start):
    """Iterative Tarjan traversal avoids recursion limits in large native rooms."""
    depth={start:0};low={start:0};parent={start:None};children={start:0};cut=set()
    stack=[(start,iter(neighbors(start,cells)))]
    while stack:
        point,edges=stack[-1]
        adjacent=next(edges,None)
        if adjacent is not None:
            if adjacent not in depth:
                parent[adjacent]=point;children[point]+=1;children[adjacent]=0
                depth[adjacent]=low[adjacent]=len(depth)
                stack.append((adjacent,iter(neighbors(adjacent,cells))))
            elif adjacent!=parent[point]:low[point]=min(low[point],depth[adjacent])
            continue
        stack.pop()
        ancestor=parent[point]
        if ancestor is None:
            if children[point]>1:cut.add(point)
        else:
            low[ancestor]=min(low[ancestor],low[point])
            if parent[ancestor] is not None and low[point]>=depth[ancestor]:cut.add(ancestor)
    if len(depth)!=len(cells):raise ValueError('Adopted room interior is disconnected')
    return cut


def irregular_aisle(shell, interior):
    door=shell['entrance_cell']
    dx,dz={'north':(0,1),'south':(0,-1),'east':(1,0),'west':(-1,0)}[shell['entrance']]
    start=(door['x']-dx,door['z']-dz)
    if start not in interior:raise ValueError('Adopted entrance no longer meets the interior')
    parent={start:None};depth={start:0};pending=deque([start])
    while pending:
        point=pending.popleft()
        for adjacent in neighbors(point,interior):
            if adjacent not in parent:
                parent[adjacent]=point;depth[adjacent]=depth[point]+1;pending.append(adjacent)
    targets=articulation_cells(interior,start)
    targets.add(max(depth,key=lambda p:(depth[p],p)))
    aisle={start}
    for point in sorted(targets):
        while point not in aisle:
            aisle.add(point);point=parent[point]
    return aisle
