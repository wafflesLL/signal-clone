"""Thin wrappers around vetted primitives from `cryptography` (pyca).

Rule for the whole project: nothing below the protocol layer is hand-rolled.
Every function here is a one-liner over pyca so the protocol code (x3dh.py,
ratchet.py) reads like the Signal specs.

Deviation from Signal (documented in the report): Signal signs prekeys with
XEdDSA using the X25519 identity key. We keep two identity keys instead, an
Ed25519 signing key and an X25519 DH key, because pyca has no XEdDSA.
"""

from __future__ import annotations

import hashlib
import hmac
import os

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import (
    Ed25519PrivateKey,
    Ed25519PublicKey,
)
from cryptography.hazmat.primitives.asymmetric.x25519 import (
    X25519PrivateKey,
    X25519PublicKey,
)
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

_RAW = serialization.Encoding.Raw
_RAW_PUB = serialization.PublicFormat.Raw
_RAW_PRIV = serialization.PrivateFormat.Raw
_NOENC = serialization.NoEncryption()

KEY_LEN = 32
NONCE_LEN = 12


# ---------------------------------------------------------------- X25519 (DH)
def dh_generate() -> X25519PrivateKey:
    return X25519PrivateKey.generate()


def dh(priv: X25519PrivateKey, pub: X25519PublicKey) -> bytes:
    """X25519 shared secret. pyca rejects the all-zero output for low-order points."""
    return priv.exchange(pub)


def dh_pub_bytes(key: X25519PrivateKey | X25519PublicKey) -> bytes:
    pub = key.public_key() if isinstance(key, X25519PrivateKey) else key
    return pub.public_bytes(_RAW, _RAW_PUB)


def dh_pub_from_bytes(data: bytes) -> X25519PublicKey:
    return X25519PublicKey.from_public_bytes(data)


def dh_priv_from_bytes(data: bytes) -> X25519PrivateKey:
    return X25519PrivateKey.from_private_bytes(data)


# ------------------------------------------------------------ Ed25519 (sigs)
def sig_generate() -> Ed25519PrivateKey:
    return Ed25519PrivateKey.generate()


def sign(priv: Ed25519PrivateKey, msg: bytes) -> bytes:
    return priv.sign(msg)


def verify(pub: Ed25519PublicKey, sig: bytes, msg: bytes) -> bool:
    from cryptography.exceptions import InvalidSignature

    try:
        pub.verify(sig, msg)
        return True
    except InvalidSignature:
        return False


def sig_pub_bytes(key: Ed25519PrivateKey | Ed25519PublicKey) -> bytes:
    pub = key.public_key() if isinstance(key, Ed25519PrivateKey) else key
    return pub.public_bytes(_RAW, _RAW_PUB)


def sig_pub_from_bytes(data: bytes) -> Ed25519PublicKey:
    return Ed25519PublicKey.from_public_bytes(data)


def sig_priv_from_bytes(data: bytes) -> Ed25519PrivateKey:
    return Ed25519PrivateKey.from_private_bytes(data)


# ----------------------------------------------------------------- KDFs
def hkdf(ikm: bytes, length: int, salt: bytes | None, info: bytes) -> bytes:
    return HKDF(algorithm=hashes.SHA256(), length=length, salt=salt, info=info).derive(ikm)


def kdf_rk(rk: bytes, dh_out: bytes, info: bytes = b"COSC483-E2EE-Ratchet") -> tuple[bytes, bytes]:
    """Double Ratchet KDF_RK: HKDF(salt=rk, ikm=dh_out) -> (new root key, chain key)."""
    out = hkdf(dh_out, 2 * KEY_LEN, salt=rk, info=info)
    return out[:KEY_LEN], out[KEY_LEN:]


def kdf_ck(ck: bytes) -> tuple[bytes, bytes]:
    """Double Ratchet KDF_CK, as recommended in the spec (sec. 5.2):
    message key = HMAC(ck, 0x01), next chain key = HMAC(ck, 0x02).
    Returns (next chain key, message key)."""
    mk = hmac.new(ck, b"\x01", hashlib.sha256).digest()
    next_ck = hmac.new(ck, b"\x02", hashlib.sha256).digest()
    return next_ck, mk


# ----------------------------------------------------------------- AEAD
def aead_encrypt(key: bytes, plaintext: bytes, ad: bytes) -> bytes:
    """AES-256-GCM with a random 96-bit nonce prepended to the ciphertext.

    The ratchet uses each message key exactly once, so a random nonce is fine.
    (The spec's alternative is deriving the nonce from the message key with HKDF.)
    """
    nonce = os.urandom(NONCE_LEN)
    return nonce + AESGCM(key).encrypt(nonce, plaintext, ad)


def aead_decrypt(key: bytes, ciphertext: bytes, ad: bytes) -> bytes:
    """Raises cryptography.exceptions.InvalidTag on tampering or wrong key/AD."""
    nonce, body = ciphertext[:NONCE_LEN], ciphertext[NONCE_LEN:]
    return AESGCM(key).decrypt(nonce, body, ad)
