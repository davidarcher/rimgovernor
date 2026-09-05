"""Local SQLite history, scoped to a selected colony, with atomic state updates."""
import json
import sqlite3
import time
from pathlib import Path


class Store:
    def __init__(self, path):
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path)
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.executescript('''
          CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, at REAL, colony TEXT, kind TEXT, data TEXT);
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

    def history(self, colony, limit=100):
        rows = self.db.execute('SELECT id,at,kind,data FROM events WHERE colony=? ORDER BY id DESC LIMIT ?', (colony, limit)).fetchall()
        return [dict(id=r[0], at=r[1], kind=r[2], **json.loads(r[3])) for r in reversed(rows)]

    def close(self):
        self.db.close()
