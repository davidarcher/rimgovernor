"""Measure local chat interpretation against fixed facts; never execute game writes."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from rimbot.config import Settings, ModelRole, load_model_routing
from rimbot.model import LocalModel
from rimbot.model_router import ModelRouter
from rimbot.player_commands import COMMAND,COMMAND_NAMES,semantic_tools,resolve_goal_id
from rimbot.store import Store

CASES = [
    ('research', 'Set research to geothermal.', {'kind':'SetResearch','project':'GeothermalPower'}),
    ('food', 'Get us to 20 days of food.', {'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20}),
    ('policy', "Stop spending components unless needed for defense.",
     {'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'defense_only'}),
    ('cancel', 'Cancel the food expansion.', {'kind':'CancelGoal','goal':'intent-food-expansion'}),
    ('cancel_supply', 'Cancel the food supply goal, not the expansion.', {'kind':'CancelGoal','goal':'EnsureFoodSupply'}),
    ('resume_supply', 'Resume the food supply goal with a target of 12 days.',
     {'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':12}),
    ('research_label', 'Set research to advanced lights.', {'kind':'SetResearch','project':'ColoredLights'}),
    ('steel_reserve', 'Keep 100 steel in reserve; otherwise allow normal spending.',
     {'kind':'ModifyResourcePolicy','resource':'Steel','reserve':100,'spending':'normal'}),
    ('explain', "Why aren't you building the workshop?", None),
]
FACTS = {
    'research_projects':[{'label':'Geothermal power','defName':'GeothermalPower'},
                         {'label':'Advanced lights','defName':'ColoredLights'}],
    'resources':{'ComponentIndustrial':10,'Steel':120},
    'goals':{'intent-food-expansion':{'label':'food expansion','status':'active'},
             'EnsureFoodSupply':{'label':'food supply','status':'blocked','cancelled':True,'target':{'food_days':20}},
             'workshop':{'status':'blocked','reason':'Requires 160 steel; only 120 is unreserved'}},
}


def normalize(request):
    request=dict(request)
    if request['kind']=='CancelGoal': request['goal']=resolve_goal_id(request['goal'],FACTS['goals'])
    if request['kind']=='SetResearch':
        matches=[p['defName'] for p in FACTS['research_projects']
                 if request['project'].casefold() in (p['label'].casefold(),p['defName'].casefold())]
        if len(matches)==1: request['project']=matches[0]
    return request


def score(answer,expected):
    calls=answer.get('tool_calls',[])
    row={'schema_failures':0,'semantic_errors':0,'unnecessary_calls':max(0,len(calls)-(1 if expected else 0)),
         'correct':False,'requests':[]}
    for call in calls:
        try:
            name=call.get('function',{}).get('name')
            if name not in COMMAND_NAMES: raise ValueError('Unsupported tool')
            request=COMMAND.validate_python(dict(json.loads(call['function']['arguments']),kind=name)).model_dump()
            row['requests'].append(request)
        except (ValueError,KeyError,TypeError): row['schema_failures']+=1
    if row['schema_failures']: return row
    try:
        # Compare the entire typed request, including default values. Extra writes
        # and unrequested numeric parameters cannot earn a passing score.
        row['correct']=(len(calls)==1 and normalize(row['requests'][0])==normalize(COMMAND.validate_python(expected).model_dump())
            if expected else not calls and all(word in (answer.get('content') or '').lower() for word in ('steel','160','120')))
    except ValueError: pass  # A valid schema with an unresolved goal is a semantic error.
    row['semantic_errors']=int(not row['correct'])
    return row


async def run(output):
    output.mkdir(parents=True,exist_ok=False)
    store=Store(output/'metrics.sqlite')
    router=ModelRouter(load_model_routing(Settings()),store,LocalModel)
    rows=[]
    async def progress(_): pass
    tools=semantic_tools()
    try:
        for identity,prompt,expected in CASES:
            start=time.monotonic()
            row={'id':identity,'prompt':prompt,'expected':expected,'schema_failures':0,'semantic_errors':0,'unnecessary_calls':0,'correct':False}
            try:
                answer,usage=await router.complete(ModelRole.STRATEGIST,[
                    {'role':'system','content':'You interpret RimWorld player commands. Use semantic tools only for '
                     'explicit orders. Explain questions from facts. The deterministic validator owns legality and '
                     'execution; never claim a requested action has completed. Facts: '+json.dumps(FACTS)},
                    {'role':'user','content':prompt}], tools, progress)
                row.update(answer=answer,usage=usage,**score(answer,expected))
            except Exception as error:
                row['error']=str(error)
            row['seconds']=round(time.monotonic()-start,3)
            rows.append(row)
            (output/'results.json').write_text(json.dumps({'cases':rows,'game_writes':0},indent=2),encoding='utf8')
            print(json.dumps({k:v for k,v in row.items() if k not in ('answer','requests','usage')}),flush=True)
    finally:
        await router.close()
        store.close()
    report={'correct':sum(r['correct'] for r in rows),'total':len(rows),
            'schema_failures':sum(r['schema_failures'] for r in rows),
            'semantic_errors':sum(r['semantic_errors'] for r in rows),
            'request_errors':sum('error' in r for r in rows),
            'unnecessary_calls':sum(r['unnecessary_calls'] for r in rows),'game_writes':0,'cases':rows}
    (output/'results.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    return report['correct']==len(CASES)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args().output)) else 1)
