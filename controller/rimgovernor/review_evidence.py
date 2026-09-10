"""Exact, review-local observations retrievable after conversational compaction."""
import json
import time
from copy import deepcopy


class ReviewEvidence:
    def __init__(self, max_bytes=2_000_000):
        self.rows={}; self.bytes=0; self.serial=0; self.evicted=0
        self.max_bytes=max_bytes

    def add(self, tool, arguments, result):
        row={'tool':tool,'arguments':deepcopy(arguments),'result':deepcopy(result),
             'captured_at':time.time()}
        size=len(json.dumps(row).encode('utf-8'))
        if size>self.max_bytes:return None
        while self.rows and self.bytes+size>self.max_bytes:
            oldest=next(iter(self.rows))
            self.bytes-=self.rows.pop(oldest)[1]; self.evicted+=1
        self.serial+=1; identity=f'e{self.serial}'
        self.rows[identity]=(row,size);self.bytes+=size
        return identity

    def index(self, query=''):
        needle=query.casefold()
        matches=[(identity,row) for identity,(row,_) in reversed(list(self.rows.items()))
                 if needle in (row['tool']+' '+json.dumps(row['arguments'])).casefold()]
        return {'items':[{'id':identity,'tool':row['tool'],'captured_at':row['captured_at'],
                         'arguments_preview':json.dumps(row['arguments'])[:240]} for identity,row in matches[:12]],
                'matching_count':len(matches),'omitted':max(0,len(matches)-12),'evicted':self.evicted,
                'note':'Historical evidence from this review, not live state. Use review_evidence to search or read; native tools refresh facts.'}

    def read(self, identity):
        if identity not in self.rows:raise ValueError('Unknown or evicted evidence ID; search the evidence index or inspect native state again')
        return dict(deepcopy(self.rows[identity][0]),id=identity,
                    historical=True,note='Exact earlier result. Game state may have changed; recheck before acting.')
