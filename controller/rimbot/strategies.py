"""Small local strategy library. Retrieval is deterministic and bounded."""
import json,re,math
from pathlib import Path
from pydantic import Field
from .contracts import Contract

class Strategy(Contract):
    id: str
    version: int = Field(ge=1)
    title: str
    tags: list[str]
    applies_when: list[str]
    approach: list[str]
    verify: list[str]
    reconsider: list[str]

class StrategyLibrary:
    def __init__(self,folder=None):
        folder=Path(folder) if folder else Path(__file__).parent/'data/strategies'
        self.entries=[Strategy.model_validate_json(p.read_text(encoding='utf-8')) for p in sorted(folder.glob('*.json'))]
        if len({s.id for s in self.entries})!=len(self.entries):raise ValueError('Duplicate strategy ID')
    def search(self,query,limit=3):
        if not 1<=limit<=5:raise ValueError('Strategy limit must be 1..5')
        words=set(re.findall(r'[a-z]{3,}',query.lower()))-{'the','and','for','with','from','that','this','colony','current'}
        ranked=[]
        for entry in self.entries:
            title=set(re.findall(r'[a-z]{3,}',(' '.join(entry.tags)+' '+entry.title).lower()))
            body=set(re.findall(r'[a-z]{3,}',entry.model_dump_json().lower()))
            score=sum((3 if w in title else 1)*math.log(1+len(self.entries)/(1+sum(w in s.model_dump_json().lower() for s in self.entries))) for w in words if w in body)
            if score:ranked.append((score,entry.id,entry))
        return [e.model_dump() for _,_,e in sorted(ranked,key=lambda x:(-x[0],x[1]))[:limit]]
