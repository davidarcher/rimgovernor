"""Bounded diagnostics outside model context. Attempts are not game actions."""
import json
import time


def bounded(value, limit=6000):
    text=json.dumps(value,ensure_ascii=False,default=str)
    return value if len(text)<=limit else {'preview':text[:limit],'truncated':True,'total_chars':len(text)}


def record(rt, call, args, result, started, outcome):
    name=call.get('function',{}).get('name','unknown')
    detail=''
    if isinstance(args,dict):
        detail=next((str(args[key]) for key in ('name','query','question','id','disposition') if args.get(key)), '')
    rt.note('planner_tool', f'{name}'+(': '+detail[:180] if detail else '')+f' · {outcome}',
        tool=name,call_id=call.get('id'),outcome=outcome,elapsed_seconds=round(time.monotonic()-started,3),
        arguments=bounded(args),result=bounded(result))
