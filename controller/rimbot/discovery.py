"""Session-scoped full-text index over native definitions; no model/embedding call."""
import re
import sqlite3
from .discovery_models import DefinitionMatch, DefinitionRead

# Language noise is not a game-rule or synonym table. Game terms come from defs.
STOP = set('a an the is are be to of for with and or in on at it this that please find show get make build place'.split())


def words(text):
    separated=re.sub(r'([a-z])([A-Z])',r'\1 \2',text)
    return [w.lower() for w in re.findall(r'[^\W_]+',separated,flags=re.UNICODE)]


class DefinitionIndex:
    def __init__(self, groups):
        self.db=sqlite3.connect(':memory:')
        self.db.execute("CREATE VIRTUAL TABLE terms USING fts5(name, label, description, tokenize='porter unicode61')")
        self.records=[]
        for group,rows in groups.items():
            if not isinstance(rows,list):continue
            for row in rows:
                if not isinstance(row,dict) or not row.get('def_name'):continue
                self.records.append((group,row))
                self.db.execute('INSERT INTO terms(rowid,name,label,description) VALUES (?,?,?,?)',
                    (len(self.records),' '.join(words(row['def_name'])),row.get('label',''),row.get('description','')))
        self.db.commit()

    def close(self):
        self.db.close()

    def search(self, text, catalog, writable, limit=8):
        tokens=[w for w in words(text) if w not in STOP][:20]
        if not tokens:return []
        # Bind the expression; quote tokens so natural language cannot inject FTS syntax.
        expression=' OR '.join('"'+t.replace('"','""')+'"' for t in dict.fromkeys(tokens))
        rows=self.db.execute('SELECT rowid,bm25(terms,6,10,1) FROM terms WHERE terms MATCH ? ORDER BY 2 LIMIT 80',(expression,)).fetchall()
        phrase=' '.join(tokens)
        def rank(hit):
            group,row=self.records[hit[0]-1]
            label=' '.join(words(row.get('label','')))
            name=' '.join(words(row['def_name']))
            exact=phrase in (label,name) or bool(label) and set(words(label))<=set(tokens)
            coverage=len(set(tokens)&set(words(label+' '+name)))/len(set(tokens))
            player_object=bool(row.get('is_weapon') or row.get('is_apparel') or row.get('is_item') or row.get('is_plant') or row.get('is_pawn') or row.get('is_building'))
            return (-coverage,-exact,-player_object,hit[1],len(label),group,row['def_name'])
        hits=[];seen=set()
        for rowid,_ in sorted(rows,key=rank):
            group,row=self.records[rowid-1];name=row['def_name']
            identity=(name,row.get('label','').casefold())
            if identity in seen:continue
            seen.add(identity)
            buildable=row.get('is_building',False) and 'construction_definitions' in catalog.available
            read=DefinitionRead(endpoint='get_def_all',arguments={},path=group,where={'def_name':name})
            if buildable:
                read=DefinitionRead(endpoint='construction_definitions',arguments={'search':name,'offset':0,'limit':32},path='',where={})
            keys=('work_to_build','cost_stuff_count','made_from_stuff','cost_list','nutrition','fertility','harvested_thing_def','harvest_yield','grow_days','min_fertility')
            facts={k:row[k] for k in keys if k in row}
            actions=['construction_place'] if buildable and 'construction_place' in writable else []
            hits.append(DefinitionMatch(group=group,def_name=name,label=row.get('label',''),description=row.get('description','')[:600],facts=facts,read=read,actions=actions))
            if len(hits)==limit:break
        return hits
