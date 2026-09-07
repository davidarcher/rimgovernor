"""Decision-boundary recording and offline request-budget reproduction."""
import argparse
import gzip
import json
import sqlite3
from pathlib import Path
from .request_budget import fit_request


def checkpoint(rt,role,model,messages,tools,thinking):
    limit=getattr(model,'context_limit',None)
    if not isinstance(limit,int):limit=getattr(rt.settings,'model_context_tokens',65536)
    name=getattr(getattr(model,'settings',rt.settings),'model',None)
    return rt.store.decision(rt.colony,role,{
        'model':name if isinstance(name,str) else None,
        'format_version':1,'observed_tick':rt.last_tick,'messages':messages,'tools':tools,
        'thinking':thinking,'context_limit':limit,
        'output_tokens':getattr(rt.settings,'max_output_tokens',8192),
        'scope':'Input to LocalModel.complete before budget fitting; not an engine snapshot or guarantee of deterministic model output.'})


def replay_budget(request):
    if request['format_version']!=1:raise ValueError('Unsupported checkpoint format')
    try:
        _,_,budget=fit_request(request['messages'],request['tools'],request['context_limit'],request['output_tokens'])
        return {'fits':True,**budget}
    except ValueError as error:return {'fits':False,'error':str(error)}


def main():
    parser=argparse.ArgumentParser(description='Read local decision checkpoints and reproduce request budgeting. No model or game calls.')
    parser.add_argument('database',type=Path)
    parser.add_argument('--decision',type=int,help='Checkpoint ID to replay; omit to list retained checkpoints')
    args=parser.parse_args()
    with sqlite3.connect(args.database.resolve().as_uri()+'?mode=ro',uri=True) as db:
        if args.decision is None:
            result=[dict(id=i,at=at,colony=c,role=r,finished=bool(done)) for i,at,c,r,done in db.execute('SELECT id,at,colony,role,result IS NOT NULL FROM decisions ORDER BY id DESC')]
        else:
            row=db.execute('SELECT payload FROM decisions WHERE id=?',(args.decision,)).fetchone()
            if row is None:parser.error('Checkpoint missing or expired')
            result=replay_budget(json.loads(gzip.decompress(row[0])))
    print(json.dumps(result,indent=2))

if __name__=='__main__':main()
