import pytest
from fastapi.testclient import TestClient
from e2ee import server

def test_relay(monkeypatch, tmp_path):
    monkeypatch.setattr(server, 'DB_PATH', str(tmp_path / 'test.sqlite3'))
    c = TestClient(server.app)
    assert c.post('/users/bob/bundle', json={'data': {'name':'public', 'opks':[{'id':1},{'id':2}]}}).status_code == 200
    assert c.get('/users/bob/bundle').json()['data']['opk']['id'] == 1
    assert c.get('/users/bob/bundle').json()['data']['opk']['id'] == 2
    assert c.get('/users/bob/bundle').json()['data']['opk'] is None
    assert c.post('/users/bob/messages', json={'data': {'ciphertext':'opaque'}}).status_code == 200
    assert c.get('/users/bob/messages').json()['messages'] == [{'ciphertext':'opaque'}]
    assert c.get('/users/bob/messages').json()['messages'] == []
