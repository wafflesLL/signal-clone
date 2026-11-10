"""Untrusted relay server (milestone 3).

It does exactly two things, and holds no secrets:
  1. Stores and serves public prekey bundles (handing out one one-time prekey per fetch).
  2. Queues opaque ciphertext envelopes per recipient until they are fetched.

Plan: a small FastAPI app with SQLite. Endpoints:
  POST /users/{id}/bundle        upload identity + SPK + batch of OPKs
  GET  /users/{id}/bundle        fetch a bundle (pops one OPK)
  POST /users/{id}/messages      enqueue envelope
  GET  /users/{id}/messages      drain inbox
The server log and DB double as evidence for the "server never sees plaintext" claim.
"""
