"""Durable requests in the existing administrator handoff field."""

def request_review(rt,key,reason):
    pending=rt.memory.setdefault('admin_requested',{})
    if not isinstance(pending,dict):pending=rt.memory['admin_requested']={}
    if pending.get(key)!=reason:
        pending[key]=reason
        rt.memory.pop('admin_retry_tick',None)
        rt.persist()


def ready(rt):
    return bool(rt.memory.get('admin_requested')) and (rt.last_tick or 0)>=rt.memory.get('admin_retry_tick',0)


def snapshot(rt):
    pending=rt.memory.get('admin_requested') or {}
    return dict(pending) if isinstance(pending,dict) else {}


def acknowledge(rt,seen):
    pending=rt.memory.get('admin_requested') or {}
    if not isinstance(pending,dict):return
    # Requests arriving or changing during inference belong to the next review.
    for key,reason in seen.items():
        if pending.get(key)==reason:pending.pop(key)
    rt.memory['admin_requested']=pending
    rt.memory.pop('admin_retry_tick',None)


def retry_later(rt):
    rt.memory['admin_retry_tick']=(rt.last_tick or 0)+2500
