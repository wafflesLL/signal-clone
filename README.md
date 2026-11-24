# COSC 483 Cryptographic Playground: E2EE Messenger

A Signal-style end-to-end encrypted messenger in C#. X3DH handles initial key agreement, and
the Double Ratchet derives a fresh key per message, giving forward secrecy and post-compromise
security. The relay server only stores public prekey bundles and forwards ciphertext.

All primitives come from vetted libraries: X25519 and Ed25519 from
[NSec](https://nsec.rocks) (libsodium), and HKDF-SHA256, HMAC-SHA256, and AES-256-GCM from
.NET's `System.Security.Cryptography`. We implement only the protocol layer.

## Layout

```
E2ee.sln
src/
  E2ee.Core/          protocol library
    Primitives.cs     library wrappers (RFC test vectors pass)
    X3dh.cs           X3DH key agreement (milestone 1)
    Ratchet.cs        Double Ratchet (milestone 2)
  E2ee.Server/        untrusted relay: ASP.NET Core + SQLite (milestone 3)
  E2ee.Client/        session manager + CLI demo (milestone 3)
tests/
  E2ee.Tests/
    PrimitivesTests.cs   RFC 7748 / 8032 / 5869 known-answer tests
    ProtocolTests.cs     X3DH and Double Ratchet properties
    ServerTests.cs       relay behavior
  vectors/               deterministic protocol vectors we generate
docs/STRATEGY.md         plan, milestones, evaluation, roles
report/                  ACM acmart LaTeX report (sigconf)
scripts/                 packaging and demo helpers
transcripts/             AI transcripts (zipped for submission)
```

## Setup

Requires the .NET 8 SDK (`brew install dotnet@8`, or https://dot.net).

```bash
dotnet build
dotnet test
```

## Demo

Start the relay in one terminal:

```bash
dotnet run --project src/E2ee.Server --urls http://127.0.0.1:8000
```

Then run the two-party demo in another:

```bash
dotnet run --project src/E2ee.Client -- --server http://127.0.0.1:8000
```

The relay writes `relay.sqlite3` (override with the `E2EE_DB` environment variable). It only
ever holds public keys and ciphertext.

## Report

Build it with `cd report && latexmk -pdf main.tex`, or upload `report/` to Overleaf, which
includes `acmart`.

## Submission checklist

- [ ] `report/main.pdf` uploaded to Canvas
- [ ] artifacts zip (`scripts/package.sh`)
- [ ] `transcripts.zip`
- [ ] Canvas comment describing AI use
