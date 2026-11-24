# Project Strategy

## What we're graded on

The grade is the **report**: five sections at 20 points each (Intro, Background, Design,
Implementation and Testing, Discussion), written in ACM LaTeX. The code is a supporting
artifact. Each phase of work below therefore ends with something that goes straight
into a report section. A failed stretch goal costs us nothing, as long as the report
explains why it failed.

Submission: report PDF, a zip of artifacts, `transcripts.zip` (we use AI), and a Canvas
comment about AI use.

## Guiding principles

1. **Build bottom-up and test each layer before starting the next.** The order is
   primitives, then X3DH, then the Double Ratchet, then the transport. The ratchet is
   only debuggable if X3DH is known to be correct. Key-agreement bugs look like "decrypt
   failed" two layers up.
2. **Every claim in the email maps to a test.** `tests/E2ee.Tests/ProtocolTests.cs` holds
   them. "Done" means the test exists and is green.
3. **The protocol is the project, and the app is a demo.** A CLI client is enough. A GUI is
   polish we only add if time remains.
4. **Mirror the spec.** Function and variable names follow the Signal pseudocode
   (`RatchetEncrypt`, `DHs`, `CKr`, `MKSKIPPED`), so a reviewer can check our code against
   the spec line by line.
5. **Record deviations from Signal the moment we make one.** That list becomes part of
   the Design section.

## Architecture

```
 Alice client                    Relay server (untrusted)                 Bob client
 ┌────────────┐   bundle fetch   ┌───────────────────────┐   bundle upload  ┌────────────┐
 │ identity   │ ───────────────▶ │ prekey bundles (pub)  │ ◀─────────────── │ identity   │
 │ ratchet[]  │   envelopes      │ inbox queues (opaque) │   envelopes      │ prekeys    │
 │ local db   │ ◀──────────────▶ │ no keys, no plaintext │ ◀──────────────▶ │ ratchet[]  │
 └────────────┘                  └───────────────────────┘                  └────────────┘
```

| Module | Role |
|---|---|
| `E2ee.Core/Primitives.cs` | Wrappers over NSec (X25519, Ed25519) and the .NET BCL (HKDF, HMAC, AES-256-GCM). **Done, and RFC vectors pass.** |
| `E2ee.Core/X3dh.cs` | Prekey bundles, `Initiate`, `Respond`. |
| `E2ee.Core/Ratchet.cs` | Double Ratchet state machine and header encoding. |
| `E2ee.Server` | ASP.NET Core minimal API and SQLite relay: bundle store and inbox queues. |
| `E2ee.Client` | Session manager and CLI chat. |

**Language choice: C# on .NET 8.** X25519 and Ed25519 come from NSec.Cryptography, a thin
binding to libsodium. HKDF-SHA256, HMAC-SHA256, and AES-256-GCM come from the .NET base class
library (`System.Security.Cryptography`), which uses the platform's crypto (OpenSSL or Apple's
CommonCrypto). We still hand-roll nothing below the protocol layer, and static types make
key/byte confusion a compile error. The project started in Python with pyca/`cryptography`
and was ported; the protocol constants and wire format did not change.

**Known deviation:** Signal signs prekeys with XEdDSA under the X25519 identity key. neither NSec nor
the BCL has XEdDSA, so each identity has two keys: Ed25519 for signing and X25519 for DH. This
is a common simplification, and the report will discuss why it is fine here.

## Milestones

Dates are relative to the start. Adjust them once the due date is known.

| # | Weeks | Deliverable | Feeds report § |
|---|---|---|---|
| 0 | 0 | Proposal approved, repo up, roles assigned. Primitives and KATs (done). | — |
| 1 | 1 | **X3DH**: both sides derive the same SK, with and without an OPK. Bad SPK signature rejected. OPK deleted after use. Cross-checked against an independent implementation. | 3, 4 |
| 2 | 2–3 | **Double Ratchet**: ping-pong, out-of-order delivery, dropped messages, MAX_SKIP, tamper and replay rejection, state rollback on failure. | 3, 4 |
| 3 | 3–4 | **Relay server and CLI client**: two terminals chatting through the server, with state persisted to disk. | 3, 4 |
| 4 | 4 | **Security demos**: forward secrecy, post-compromise security, server-only-sees-ciphertext. Put them in tests and in a scripted demo. | 4 |
| 5 | 5 | **Stretch**: safety numbers first (cheap and visible), then group messaging (sender keys). | 4, 5 |
| 6 | 5–6 | Report writing, figures, artifact zip, transcripts. | all |

Write the report continuously. Background (§2) can be written during week 1, while
reading the specs. Design (§3) gets drafted as soon as milestone 2's interfaces settle.

## Correctness strategy ("test vectors")

Signal doesn't publish official X3DH or Double Ratchet test vectors, so the email's
"test against known test vectors" becomes three layers:

1. **Primitive KATs** from RFC 7748, 8032, and 5869. These are in `tests/E2ee.Tests/PrimitivesTests.cs`
   and already pass.
2. **Protocol known-answer and interop**: fix every private key, then compare our SK and
   message keys against an independent implementation, such as the `x3dh` and `doubleratchet`
   PyPI packages (or libsignal) configured with the same KDF parameters, or a teammate's separate script.
   That turns the protocol into deterministic vectors we can commit under `tests/vectors/`.
3. **Property tests**: randomized delivery orders and drops (seeded), tampering, replay.

The report should say this plainly: "no official vectors exist, so we did X". A reviewer
trusts a plain statement like that more than an overclaim.

## Security evaluation (the demos)

- **Forward secrecy**: capture ciphertexts, then snapshot a victim's full state at time *t*.
  Show that no ciphertext from before *t* decrypts with anything in the snapshot, because
  those message keys were deleted.
- **Post-compromise security**: the attacker clones Alice's state at *t*. Plot "can the
  attacker decrypt message *i*?" against *i*. Access drops to zero after one DH ratchet
  round trip. That plot makes a good figure for §4.
- **Untrusted server**: run a full conversation and grep the server DB and logs for the
  plaintexts. There should be zero hits. Also show the server can't tell which messages
  are first-contact beyond what the headers reveal, and be honest about metadata
  (who talks to whom and when) staying visible.

## Team split (3–5 people)

| Role | Owns |
|---|---|
| Protocol A | `X3dh.cs` and its tests, then helps with the ratchet. |
| Protocol B | `Ratchet.cs` and its tests. |
| Systems | `E2ee.Server`, `E2ee.Client`, persistence, demo script. |
| Evaluation / report lead | Cross-check vectors, security demos, figures, report assembly. |
| (5th) Stretch | Safety numbers and the group-messaging research spike. |

Everyone writes their own subsection of §3 and §4. One person edits for a single voice.

## Risks

| Risk | Mitigation |
|---|---|
| Ratchet edge cases (skipped keys across DH steps) eat weeks | Port the spec's pseudocode literally first, then refactor. The tests already exist. |
| No reference vectors | Use the interop cross-check described above. |
| Group messaging is too big | It's a stretch goal. Do safety numbers first. If groups slip, the report covers why (sender keys vs. MLS) in §5. |
| Report crunch | Write §2 in week 1 and §3 by week 3. |

## Workflow

- Use `main` plus short feature branches and PRs, with one reviewer per PR, ideally the
  other protocol person.
- Run `dotnet test` before every merge. Add a test in the same PR as the code it covers.
- Save AI transcripts as you go into `transcripts/`, then zip it at the end.
