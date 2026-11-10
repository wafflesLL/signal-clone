"""Double Ratchet.

Spec: https://signal.org/docs/specifications/doubleratchet/  (section 3 is the
reference pseudocode, and the function names below mirror it)

Status: interfaces only. Milestone 2 fills these in (see docs/STRATEGY.md).
"""

from __future__ import annotations

from dataclasses import dataclass, field

MAX_SKIP = 1000  # cap on stored skipped message keys per chain (DoS protection, sec. 3.2)


@dataclass
class Header:
    dh_pub: bytes  # sender's current ratchet public key
    pn: int  # number of messages in the sender's previous sending chain
    n: int  # message number in the current sending chain

    def encode(self) -> bytes:
        return self.dh_pub + self.pn.to_bytes(4, "big") + self.n.to_bytes(4, "big")

    @classmethod
    def decode(cls, data: bytes) -> "Header":
        return cls(data[:32], int.from_bytes(data[32:36], "big"), int.from_bytes(data[36:40], "big"))


@dataclass
class RatchetState:
    DHs: object  # our ratchet keypair (X25519PrivateKey)
    DHr: bytes | None  # their ratchet public key
    RK: bytes
    CKs: bytes | None
    CKr: bytes | None
    Ns: int = 0
    Nr: int = 0
    PN: int = 0
    MKSKIPPED: dict[tuple[bytes, int], bytes] = field(default_factory=dict)


def init_alice(sk: bytes, bob_dh_pub: bytes) -> RatchetState:
    """RatchetInitAlice (sec. 3.3). bob_dh_pub is Bob's signed prekey from X3DH."""
    raise NotImplementedError


def init_bob(sk: bytes, bob_dh_keypair: object) -> RatchetState:
    """RatchetInitBob (sec. 3.3)."""
    raise NotImplementedError


def encrypt(state: RatchetState, plaintext: bytes, ad: bytes) -> tuple[Header, bytes]:
    """RatchetEncrypt. The header is authenticated as part of the AD."""
    raise NotImplementedError


def decrypt(state: RatchetState, header: Header, ciphertext: bytes, ad: bytes) -> bytes:
    """RatchetDecrypt: try skipped keys, run the DH ratchet if the header key is new,
    skip ahead (bounded by MAX_SKIP), then decrypt.

    Must leave the state unchanged if authentication fails (the spec's
    'state rollback on failure' note). The simplest way: work on a deepcopy and
    commit it only after decryption succeeds.
    """
    raise NotImplementedError
