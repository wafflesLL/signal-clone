# Project Strategy

## Goal

We're building a **usable end-to-end encrypted messenger**: a desktop app that two people on
different computers install, sign up in, add each other, and chat in. Every message goes
through the X3DH and Double Ratchet code in this repo, and the relay only ever sees
ciphertext. The protocol is the core of the work. The app is how we show it works for real
people and not just in unit tests.

"Usable" means all of these:

- Install and run the app on macOS and Windows (Linux if it comes for free) without a terminal.
- Pick a username and register with a relay. The app generates and uploads keys on its own.
- Add a contact by username, then send and receive messages in real time.
- Quit and reopen the app and keep identity, sessions, and history.
- Compare a safety number with a contact to check that nobody is in the middle.
- See an error, not a crash, when the relay is down or a message fails to decrypt.

## What we're graded on

The grade is still the **report**: five sections at 20 points each (Intro, Background,
Design, Implementation and Testing, Discussion), written in ACM LaTeX. A usable app makes
the report stronger, not weaker. It gives §3 a real system architecture, gives §4 an
end-to-end evaluation and screenshots, and gives §5 real lessons about deploying a secure
protocol. Each phase below still ends with something that goes straight into a report
section. A failed stretch goal costs us nothing, as long as the report explains why it
failed.

Submission: report PDF, a zip of artifacts (source plus built app), `transcripts.zip` (we
use AI), and a Canvas comment about AI use.

## Guiding principles

1. **Build bottom-up and test each layer before starting the next.** The order is
   primitives, X3DH, Double Ratchet, session layer, then UI. The UI never calls crypto
   directly; it calls the session layer. Key-agreement bugs look like "decrypt failed" two
   layers up, so every layer below the UI has to be covered by tests.
2. **Every security claim maps to a test.** Protocol claims live in
   `tests/E2ee.Tests/ProtocolTests.cs` and session/relay claims in their own test files.
   "Done" means the test exists and is green.
3. **The app is the product and the protocol is the core.** Every feature goes through
   the real protocol. There are no "demo mode" shortcuts that skip encryption.
4. **Keep the UI thin.** The headless CLI and the desktop app share the same session
   library. If a feature works in the CLI, the UI only has to render it, and the CLI stays
   our scripted test harness and demo tool.
5. **Mirror the spec.** Function and variable names follow the Signal pseudocode
   (`RatchetEncrypt`, `DHs`, `CKr`, `MKSKIPPED`), so a reviewer can check our code against
   the spec line by line.
6. **Record deviations from Signal the moment we make one.** That list becomes part of
   the Design section.

## Architecture

```
 Alice: E2ee.App (desktop)          Relay: E2ee.Server (untrusted)          Bob: E2ee.App
 ┌──────────────────────┐          ┌───────────────────────────┐          ┌──────────────────────┐
 │ UI (Avalonia, MVVM)  │          │ accounts: user → id keys  │          │ UI (Avalonia, MVVM)  │
 │──────────────────────│  HTTPS   │ prekey bundles (public)   │  HTTPS   │──────────────────────│
 │ E2ee.Messaging       │ ◀──────▶ │ inbox queues (opaque)     │ ◀──────▶ │ E2ee.Messaging       │
 │  sessions, contacts, │ +push    │ signed-request auth       │ +push    │  sessions, contacts, │
 │  local encrypted db  │          │ no secrets, no plaintext  │          │  local encrypted db  │
 │──────────────────────│          └───────────────────────────┘          │──────────────────────│
 │ E2ee.Core (protocol) │                                                 │ E2ee.Core (protocol) │
 └──────────────────────┘                                                 └──────────────────────┘
```

| Project | Role | Status |
|---|---|---|
| `E2ee.Core` | Protocol library: `Primitives.cs` (NSec + .NET BCL wrappers), `X3dh.cs`, `Ratchet.cs`. | Implemented; RFC vectors and protocol tests pass. |
| `E2ee.Messaging` | Session layer shared by the app and CLI: account setup, prekey upload and refill, contact list, session manager (first message vs. ongoing), local storage, relay client, safety numbers. | Done. |
| `E2ee.Server` | ASP.NET Core and SQLite relay: accounts, bundle store, inbox queues, real-time push. | Done: accounts, signed-request auth, acknowledged inboxes, long-poll push. |
| `E2ee.App` | Avalonia desktop app: onboarding, conversation list, chat view, group chats, safety-number screen. | Done: sign-up, contacts, chat, groups, unread badges, relay status, safety numbers, key-change review. No settings screen yet. |
| `E2ee.Client` | Headless CLI over `E2ee.Messaging`, for scripted demos and integration tests. | Done. |

**Language choice: C# on .NET 8.** X25519 and Ed25519 come from NSec.Cryptography, a thin
binding to libsodium. HKDF-SHA256, HMAC-SHA256, and AES-256-GCM come from the .NET base class
library (`System.Security.Cryptography`), which uses the platform's crypto (OpenSSL or Apple's
CommonCrypto). We still hand-roll nothing below the protocol layer, and static types make
key/byte confusion a compile error. The project started in Python with pyca/`cryptography`
and was ported; the protocol constants and wire format did not change.

**UI choice: Avalonia.** It's a cross-platform .NET desktop UI that builds for macOS,
Windows, and Linux from one codebase with no extra platform workloads, and it uses the
MVVM pattern, which keeps the UI testable and thin. We considered .NET MAUI, which also
reaches iOS and Android, but on macOS it needs Xcode and Mac Catalyst workloads and it has
no Linux target. Mobile is out of scope; the report's §5 can discuss it.

**Real-time delivery.** Long polling: the client holds a signed `GET /v1/messages/wait`
open for up to 20 seconds, and the relay answers the moment mail arrives. The answer is
only "you have mail" with no content; the client then fetches it from the normal signed
endpoint. The client passes the highest inbox id it has seen, so mail it is holding (see
Sessions) doesn't wake it again. We planned SignalR (WebSockets), but its handshake can't
easily carry our per-request signatures, so it would have needed a second authentication
scheme. Long polling delivers just as instantly at chat scale and reuses the one we have.

**Safety numbers.** 60 digits computed from both identities, following Signal's
fingerprint format (iterated SHA-512 over each party's keys and username; the two halves
are sorted so both sides see the same number). The app and the CLI show them. Marking a
contact verified is stored locally. A key change clears it, raises a warning, pauses
sending, and holds the contact's new messages on the relay (unacknowledged) until the user
reviews the new number and trusts the key. The user trusts exactly the key they reviewed,
not whatever the relay holds at that moment.

**Group chats.** Client-side fan-out, the way Signal handled groups before Sender Keys:
a group message is encrypted separately for each member over the existing pairwise Double
Ratchet sessions, with the group's random 128-bit id inside the ciphertext. The relay sees
ordinary one-to-one messages and never learns that a group exists, who is in it, or which
messages belong to it (it can still guess from timing, since one send produces several
messages at once). So groups get the same forward secrecy, post-compromise security, and
key-change protection as direct chats, and need no new cryptography. Every member keeps
their own member list. Creating a group or adding someone sends a `group_update` with the
name and full member list to everyone; leaving sends `group_leave`. A client accepts group
messages and updates only from people currently in its copy of the list, so knowing a
group's id isn't enough to post in it. Updates can only add people, so no member can kick
another out; the only way out is to leave. Sending to a group is blocked while any member's
safety number change is unresolved (leaving is not: it skips that member). The cost is N−1
encryptions and uploads per message, which is fine for the 50-person cap.

**Photos, videos, and reactions.** Media is stored on the users' devices; the relay only
passes it along. The sender reads the file, picks a fresh random 256-bit key, encrypts it
with AES-256-GCM (associated data `COSC483-ATTACHMENT-v1`), and uploads the ciphertext to
`POST /v1/attachments?to=…`, naming who may fetch it. The ratchet message then carries a
pointer: the relay's random id, the key, the SHA-256 of the ciphertext, the size, and the
file name. Each recipient downloads with a signed request (the relay serves it only to the
named recipients), checks the hash and size, decrypts, writes the file into the profile's
`media/` folder encrypted under the master key, and acknowledges. The relay deletes the blob
once every recipient has acknowledged it, or after 14 days. So the relay never sees a key,
a file name, or plaintext, and holds ciphertext only in transit. One upload serves a whole
group, while the pointer is fanned out like any group message. Only images and videos are
accepted (up to 100 MB), and the type is always taken from our own list by file extension,
never from what the sender claims; when a file is opened in another app, its extension comes
from that type too, so a message can't smuggle in a program disguised as a video. Opening a
file writes a plaintext copy to the profile's `open/` folder, which is emptied when the app
closes (a documented weak spot: other apps can read it while it's there).

A reaction is a small ratchet message naming the target by its author and send timestamp
(as Signal does), plus the emoji, or none to take it back. Each person has at most one
reaction per message. In groups, reactions are accepted only from members, and in direct
chats only for messages between the two people.

**Relay authentication.** Usernames are bound to an Ed25519 identity key on first
registration (trust on first use at the server). Every request is signed with that key over
the method, path, username, a millisecond timestamp, and a hash of the body. The relay
rejects timestamps more than five minutes off and any signature it has already seen. The
server holds no secrets. Inbox messages stay queued until the recipient acknowledges them,
so a client crash loses nothing.

**Local storage.** Each app keeps its identity keys, prekeys, ratchet states, contacts,
and message history in a local SQLite file. Private keys, ratchet state, message bodies, and
queued envelopes are encrypted at rest (AES-256-GCM, with the row name as associated data)
under a per-profile master key held by the OS (macOS Keychain, Windows DPAPI, or an
owner-only key file on Linux). Usernames, contact public keys, and timestamps are not
encrypted. Ratchet state is written in the same transaction as the message it decrypted,
and the inbox item is acknowledged only after that commit, so a crash can neither lose a
message nor apply one twice.

**Sessions.** A peer can have several sessions (at most five are kept). An incoming
message carrying X3DH fields picks its session by the sender's ephemeral key; any other
message is tried against each session, newest first. When both people start a session at
the same moment, this converges on one session after a round trip, which is the same
approach Signal takes. The initiator keeps resending the X3DH fields until the first reply.
A first message from a stranger is accepted only if its identity key matches the one the
relay has registered for the sender, and a known contact whose key changes is rejected
until the user runs `trust`.

**Known deviations from Signal:**
- Safety numbers cover both of our identity keys (Ed25519 and X25519) and use their own
  version tag, so they're not comparable with Signal's.
- After a key change Signal shows the new messages with a warning. We hold them until the
  user decides. That's stricter, but someone who reinstalls is unreadable until the other
  person reviews the new key.
- Push is long polling, not a persistent socket (see Real-time delivery).
- The relay authenticates clients with signatures from the identity key instead of a
  password, so one key does both jobs. Simpler, but compromising the identity key also lets
  an attacker read (still encrypted) inbox traffic.
- Signal signs prekeys with XEdDSA under the X25519 identity key. Neither NSec nor the BCL
  has XEdDSA, so each identity has two keys: Ed25519 for signing and X25519 for DH. This
  is a common simplification, and the report will discuss why it is fine here.
- Groups use client-side fan-out, not Sender Keys or Signal's private group system
  (zero-knowledge group credentials), and not MLS. Membership is agreed only among the
  members, with no server enforcing it, so a malicious member could tell different people
  different member lists. Removing someone else from a group isn't supported.
- Attachments use AES-256-GCM with a random key plus a SHA-256 of the ciphertext, where
  Signal uses AES-CBC with HMAC-SHA256 and a digest. Both give integrity; ours needs no
  padding. Files are read into memory whole, which is why the limit is 100 MB.
- One device per account. Signal's multi-device support (Sesame) is out of scope.
- Contact discovery is by exact username, not by phone number.

## Milestones

Dates are relative to the start. Adjust them once the due date is known.

| # | Weeks | Deliverable | Feeds report § | Status |
|---|---|---|---|---|
| 0 | 0 | Proposal approved, repo up, roles assigned. Primitives and KATs. | — | Done |
| 1 | 1 | **X3DH**: both sides derive the same SK, with and without an OPK. Bad SPK signature rejected. OPK deleted after use. Cross-checked against an independent implementation. | 3, 4 | Done except the cross-check |
| 2 | 1–2 | **Double Ratchet**: ping-pong, out-of-order delivery, dropped messages, MAX_SKIP, tamper and replay rejection, state rollback on failure. | 3, 4 | Done |
| 3 | 2–3 | **Session layer and authenticated relay**: `E2ee.Messaging` with persistent encrypted local state, account registration, signed relay requests, prekey refill. The CLI rebuilt on it: two terminals on two machines chatting, surviving restarts. | 3, 4 | Done (tested on one machine; try two machines on a LAN) |
| 4 | 3–4 | **Desktop app MVP**: Avalonia app with onboarding, contact list, chat view, and polling delivery. Two people on two laptops can chat. | 3, 4 | Done; tested headlessly. Still to do: a hands-on run on macOS and Windows, and two laptops |
| 5 | 4–5 | **Trust and polish**: safety-number screen, key-change warnings, SignalR push, error states, packaged builds (`.app` and `.exe`) attached to a GitHub release. | 3, 4 | Done, with long polling instead of SignalR. Builds are made by `scripts/package-app.sh` but not yet attached to a GitHub release. Not yet tested on a real screen or on Windows |
| 6 | 4–5 | **Security demos** in tests and a scripted CLI demo: forward secrecy, post-compromise security, server-only-sees-ciphertext. Run a small usability check with a few classmates. | 4 | |
| 7 | 5 | **Stretch**: group messaging, then attachments (encrypt the file, send the key in a message). | 4, 5 | Done early: group chats with client-side fan-out instead of sender keys, photos and videos stored on devices (relay holds ciphertext only in transit), and reactions |
| 8 | 5–6 | Report writing, figures and screenshots, artifact zip, transcripts. | all | |

Write the report continuously. Background (§2) can be written while reading the specs,
and Design (§3) gets drafted as soon as milestone 3's interfaces settle.

## Correctness strategy ("test vectors")

Signal doesn't publish official X3DH or Double Ratchet test vectors, so the email's
"test against known test vectors" becomes five layers:

1. **Primitive KATs** from RFC 7748, 8032, and 5869. These are in
   `tests/E2ee.Tests/PrimitivesTests.cs` and pass. Safety numbers are checked against a
   separate Python implementation (`scripts/safety_number_crosscheck.py`).
2. **Protocol known-answer and interop**: fix every private key, then compare our SK and
   message keys against an independent implementation, such as the `x3dh` and
   `doubleratchet` PyPI packages (or libsignal) configured with the same KDF parameters,
   or a teammate's separate script. That turns the protocol into deterministic vectors we
   can commit under `tests/vectors/`.
3. **Property tests**: randomized delivery orders and drops (seeded), tampering, replay.
4. **End-to-end tests**: two `E2ee.Messaging` instances talking through an in-process
   relay (`WebApplicationFactory`), covering first contact, restarts mid-conversation,
   OPK exhaustion, an unauthenticated inbox read being rejected, and a contact's identity
   key changing. These are in `MessagingTests.cs` and `RelayAuthTests.cs` and pass.
5. **UI tests**: the real Avalonia views rendered headlessly (Skia) and driven with
   keyboard input against an in-process relay (`AppTests.cs`). They also produce the
   screenshots in `docs/screenshots/`.

The report should say this plainly: "no official vectors exist, so we did X". A reviewer
trusts a plain statement like that more than an overclaim.

## Security evaluation (the demos)

- **Forward secrecy**: capture ciphertexts, then snapshot a victim's full state at time *t*.
  Show that no ciphertext from before *t* decrypts with anything in the snapshot, because
  those message keys were deleted.
- **Post-compromise security**: the attacker clones Alice's state at *t*. Plot "can the
  attacker decrypt message *i*?" against *i*. Access drops to zero after one DH ratchet
  round trip. That plot makes a good figure for §4.
- **Untrusted server**: run a full conversation in the app and grep the server DB and logs
  for the plaintexts. There should be zero hits. Be honest that metadata (who talks to
  whom and when) stays visible to the relay. `ImageArrivesAndOnlyDevicesKeepIt` already
  checks this for a photo: the relay's copy is gone once delivered, and the plaintext
  appears nowhere in the recipient's profile. `TamperedBlobIsDiscarded` shows a relay that
  alters a file is caught.
- **Active attacker at the relay**: show that a relay that swaps in its own prekey bundle
  is caught when the users compare safety numbers, and that the app warns when a known
  contact's identity key changes. Already covered by tests (`RelaySwappingIdentityKeyIsDetected`,
  `TrustUsesTheReviewedKeyNotTheRelaysLatest`, `ReviewAndTrustAChangedKey`); milestone 6
  turns them into a scripted demo.
- **Usability**: a short task-based check with a few classmates (install, add a contact,
  send a message, verify a safety number). Note where people got stuck. That goes in §4
  and §5.

## Team split (3–5 people)

| Role | Owns |
|---|---|
| Protocol | `E2ee.Core` and its tests, the interop vectors, and the security demos. |
| Session layer | `E2ee.Messaging`: local storage, session manager, relay client, safety numbers. |
| Server | `E2ee.Server`: accounts, signed-request auth, push, deployment. |
| App | `E2ee.App`: UI, onboarding, packaging. |
| (5th) Evaluation / report lead | Usability check, figures, report assembly. Otherwise the protocol owner takes this on. |

Everyone writes their own subsection of §3 and §4. One person edits for a single voice.

## Risks

| Risk | Mitigation |
|---|---|
| UI work eats the time for protocol and evaluation | The session layer and CLI come first (milestone 3), so the security story is complete before any UI. The UI stays a thin MVVM layer. |
| State desync after a crash or a failed decrypt | Decrypt on a copy and commit atomically with the stored message (the ratchet already rolls back). End-to-end restart tests. |
| Relay impersonation or inbox theft | Signed requests bound to the identity key, plus safety numbers for checking out of band. |
| Private keys leaked from disk | Encrypt them at rest with an OS-keychain key. Say in the report what this does not protect against (malware running as the user). |
| Cross-platform packaging pain (macOS signing, Gatekeeper) | Ship unsigned builds with run instructions. Signing is a stretch goal. Test on both OSes from milestone 4 onward. |
| No reference vectors | Use the interop cross-check described above. |
| Group messaging is too big | Done with client-side fan-out, which reuses the pairwise sessions. The report's §5 compares it with sender keys and MLS. |
| Report crunch | Write §2 early and §3 by the end of milestone 3. Take screenshots as the app grows. |

## Workflow

- Use `main` plus short feature branches and PRs, with one reviewer per PR.
- Run `dotnet test` before every merge. Add a test in the same PR as the code it covers.
- Try the app on both macOS and Windows before merging a UI change.
- Save AI transcripts as you go into `transcripts/`, then zip it at the end.
