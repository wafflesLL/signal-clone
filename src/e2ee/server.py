"""Local demonstration relay. No client secrets are accepted or stored."""
import json
import os
import sqlite3
from contextlib import contextmanager
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

DB_PATH = os.environ.get('E2EE_DB', 'relay.sqlite3')
app = FastAPI(title='COSC483 E2EE relay')

@contextmanager
def db():
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.execute('CREATE TABLE IF NOT EXISTS bundles (user TEXT PRIMARY KEY, payload TEXT NOT NULL)')
        conn.execute('CREATE TABLE IF NOT EXISTS inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, recipient TEXT NOT NULL, payload TEXT NOT NULL)')
        conn.commit()
        yield conn
        conn.commit()
    finally:
        conn.close()

class Payload(BaseModel):
    data: dict

@app.post('/users/{user}/bundle')
def upload_bundle(user: str, payload: Payload):
    with db() as c:
        c.execute('INSERT OR REPLACE INTO bundles VALUES (?, ?)', (user, json.dumps(payload.data)))
    return {'ok': True}

@app.get('/users/{user}/bundle')
def get_bundle(user: str):
    with db() as c:
        row = c.execute('SELECT payload FROM bundles WHERE user=?', (user,)).fetchone()
        if not row:
            raise HTTPException(404, 'unknown user')
        data = json.loads(row[0])
        opks = data.get('opks', [])
        opk = opks.pop(0) if opks else None
        c.execute('UPDATE bundles SET payload=? WHERE user=?', (json.dumps(data), user))
        return {'data': {**{k:v for k,v in data.items() if k != 'opks'}, 'opk': opk}}

@app.post('/users/{user}/messages')
def enqueue(user: str, payload: Payload):
    with db() as c:
        c.execute('INSERT INTO inbox(recipient,payload) VALUES (?,?)', (user, json.dumps(payload.data)))
    return {'ok': True}

@app.get('/users/{user}/messages')
def drain(user: str):
    with db() as c:
        rows = c.execute('SELECT id,payload FROM inbox WHERE recipient=? ORDER BY id', (user,)).fetchall()
        if rows:
            c.executemany('DELETE FROM inbox WHERE id=?', [(r[0],) for r in rows])
        return {'messages': [json.loads(r[1]) for r in rows]}
