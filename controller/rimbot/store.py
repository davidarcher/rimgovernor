"""Local SQLite history, scoped to a selected colony, with atomic state updates."""
import json
import sqlite3
import time
import gzip
from pathlib import Path


class Store:
    def __init__(self, path):
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path)
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.executescript('''
          CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, at REAL, colony TEXT, kind TEXT, data TEXT);
          CREATE TABLE IF NOT EXISTS decisions(id INTEGER PRIMARY KEY, at REAL, colony TEXT, role TEXT, payload BLOB, result BLOB);
        ''')

    def get(self, key, default=None):
        row = self.db.execute('SELECT value FROM state WHERE key=?', (key,)).fetchone()
        return json.loads(row[0]) if row else default

    def set(self, key, value):
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO state VALUES (?,?)', (key, json.dumps(value)))

    def event(self, colony, kind, **data):
        at = time.time()
        with self.db:
            cur = self.db.execute('INSERT INTO events(at,colony,kind,data) VALUES (?,?,?,?)', (at, colony, kind, json.dumps(data)))
        return dict(id=cur.lastrowid, at=at, kind=kind, **data)

    def history(self, colony, limit=100, include_diagnostics=False):
        diagnostic_filter = '' if include_diagnostics else " AND kind NOT IN ('model_diagnostic','model_call','tool_result','planner_tool')"
        rows = self.db.execute('SELECT id,at,kind,data FROM events WHERE colony=?'+diagnostic_filter+' ORDER BY id DESC LIMIT ?', (colony, limit)).fetchall()
        return [dict(id=r[0], at=r[1], kind=r[2], **json.loads(r[3])) for r in reversed(rows)]

    def close(self):
        self.db.close()

    def decision(self,colony,role,payload):
        packed=gzip.compress(json.dumps(payload,ensure_ascii=False).encode('utf-8'))
        with self.db:
            row=self.db.execute('INSERT INTO decisions(at,colony,role,payload) VALUES (?,?,?,?)',(time.time(),colony,role,packed))
            identity=row.lastrowid
            # Bound disk use across sessions as well as the current colony.
            self.db.execute('DELETE FROM decisions WHERE id NOT IN (SELECT id FROM decisions ORDER BY id DESC LIMIT 64)')
        return identity

    def finish_decision(self,identity,result):
        with self.db:
            self.db.execute('UPDATE decisions SET result=? WHERE id=?',(gzip.compress(json.dumps(result,ensure_ascii=False).encode('utf-8')),identity))

    def read_decision(self,identity):
        row=self.db.execute('SELECT id,at,colony,role,payload,result FROM decisions WHERE id=?',(identity,)).fetchone()
        if row is None:raise KeyError('Decision checkpoint is missing or expired')
        return dict(id=row[0],at=row[1],colony=row[2],role=row[3],request=json.loads(gzip.decompress(row[4])),
                    result=json.loads(gzip.decompress(row[5])) if row[5] else None)
