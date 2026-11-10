"""Client: session management and the CLI chat (milestone 3).

Responsibilities:
  - Keep local state (identity keys, prekey store, one RatchetState per peer) on disk.
  - First message to a peer: fetch bundle -> x3dh.initiate -> ratchet.init_alice.
  - First message from a peer: x3dh.respond -> ratchet.init_bob.
  - Everything after: ratchet.encrypt / ratchet.decrypt.
"""
