# COSC 483 Cryptographic Playground: E2EE Messenger

A Signal-style end-to-end encrypted messenger. X3DH handles initial key agreement, and the
Double Ratchet derives a fresh key per message, giving forward secrecy and post-compromise
security. The relay server only stores public prekey bundles and forwards ciphertext.

All primitives come from [pyca/cryptography](https://cryptography.io): X25519, Ed25519,
HKDF-SHA256, HMAC-SHA256, and AES-256-GCM. We implement only the protocol layer.

## Layout

```
src/e2ee/
  primitives.py   pyca wrappers (done, RFC test vectors pass)
  x3dh.py         X3DH key agreement (milestone 1)
  ratchet.py      Double Ratchet (milestone 2)
  server.py       untrusted relay (milestone 3)
  client.py       session manager + CLI (milestone 3)
tests/
  test_primitives.py   RFC 7748 / 8032 / 5869 known-answer tests
  test_protocol.py     protocol and security properties (skipped until implemented)
  vectors/             deterministic protocol vectors we generate
docs/STRATEGY.md       plan, milestones, evaluation, roles
report/                ACM acmart LaTeX report (sigconf)
scripts/               packaging and demo helpers
transcripts/           AI transcripts (zipped for submission)
```

## Setup

Requires Python 3.11 or newer.

```bash
python3 -m venv .venv
.venv/bin/pip install -e ".[dev]"
.venv/bin/pytest
```

## Report

Build it with `cd report && latexmk -pdf main.tex`, or upload `report/` to Overleaf, which
includes `acmart`.

## Submission checklist

- [ ] `report/main.pdf` uploaded to Canvas
- [ ] artifacts zip (`scripts/package.sh`)
- [ ] `transcripts.zip`
- [ ] Canvas comment describing AI use
