"""X3DH initial key agreement.

Spec: https://signal.org/docs/specifications/x3dh/  (section numbers below refer to it)

Status: interfaces only. Milestone 1 fills these in (see docs/STRATEGY.md).
"""

from __future__ import annotations

from dataclasses import dataclass, field

INFO = b"COSC483-E2EE-X3DH"
# Sec. 2.2: F is 32 0xFF bytes for X25519, prepended to the DH concatenation.
F = b"\xff" * 32


@dataclass
class IdentityKeys:
    """Long-term identity: Ed25519 for signing and X25519 for DH (see primitives.py)."""

    sig_priv: object  # Ed25519PrivateKey
    dh_priv: object  # X25519PrivateKey


@dataclass
class PrekeyBundle:
    """What the server stores and hands out. Public keys only (sec. 3.2)."""

    identity_sig_pub: bytes
    identity_dh_pub: bytes
    signed_prekey_id: int
    signed_prekey_pub: bytes
    signed_prekey_sig: bytes  # Sig(IK, Encode(SPK))
    one_time_prekey_id: int | None = None
    one_time_prekey_pub: bytes | None = None


@dataclass
class LocalPrekeyStore:
    """The private halves that Bob keeps. One-time prekeys are deleted once used (sec. 3.4)."""

    identity: IdentityKeys
    signed_prekeys: dict[int, object] = field(default_factory=dict)
    one_time_prekeys: dict[int, object] = field(default_factory=dict)


@dataclass
class InitialMessage:
    """The header of Alice's first message (sec. 3.3)."""

    identity_sig_pub: bytes
    identity_dh_pub: bytes
    ephemeral_pub: bytes
    signed_prekey_id: int
    one_time_prekey_id: int | None
    ciphertext: bytes


def generate_identity() -> IdentityKeys:
    raise NotImplementedError


def generate_prekeys(identity: IdentityKeys, n_one_time: int = 20) -> tuple[LocalPrekeyStore, list[PrekeyBundle]]:
    raise NotImplementedError


def initiate(alice: IdentityKeys, bundle: PrekeyBundle) -> tuple[bytes, bytes, object]:
    """Alice's side (sec. 3.3). Verify the SPK signature, compute
    DH1..DH4, and derive SK = HKDF(F || DH1 || DH2 || DH3 [|| DH4]).

    Returns (SK, AD, ephemeral_priv). AD = Encode(IK_A) || Encode(IK_B).
    Must raise if the signature is invalid.
    """
    raise NotImplementedError


def respond(bob: LocalPrekeyStore, msg: InitialMessage) -> tuple[bytes, bytes]:
    """Bob's side (sec. 3.4). Recompute SK, then delete the one-time prekey that was used.
    Returns (SK, AD)."""
    raise NotImplementedError
