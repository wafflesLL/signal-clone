"""Known-answer tests for the primitive layer (RFC 7748, RFC 8032, RFC 5869)."""

import pytest
from cryptography.exceptions import InvalidTag

from e2ee import primitives as P

h = bytes.fromhex


def test_x25519_rfc7748_sec6_1():
    a = P.dh_priv_from_bytes(h("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"))
    b = P.dh_priv_from_bytes(h("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"))
    assert P.dh_pub_bytes(a) == h("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")
    assert P.dh_pub_bytes(b) == h("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
    shared = h("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742")
    assert P.dh(a, b.public_key()) == shared
    assert P.dh(b, a.public_key()) == shared


def test_ed25519_rfc8032_test1():
    sk = P.sig_priv_from_bytes(h("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"))
    assert P.sig_pub_bytes(sk) == h("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")
    sig = P.sign(sk, b"")
    assert sig == h(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555"
        "fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
    )
    assert P.verify(sk.public_key(), sig, b"")
    assert not P.verify(sk.public_key(), sig, b"x")


def test_hkdf_rfc5869_case1():
    okm = P.hkdf(
        ikm=b"\x0b" * 22,
        length=42,
        salt=h("000102030405060708090a0b0c"),
        info=h("f0f1f2f3f4f5f6f7f8f9"),
    )
    assert okm == h("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865")


def test_kdf_ck_separates_keys():
    ck2, mk = P.kdf_ck(b"\x00" * 32)
    assert ck2 != mk and len(ck2) == len(mk) == 32


def test_aead_roundtrip_and_tamper():
    key = b"\x01" * 32
    ct = P.aead_encrypt(key, b"hello", b"ad")
    assert P.aead_decrypt(key, ct, b"ad") == b"hello"
    with pytest.raises(InvalidTag):
        P.aead_decrypt(key, ct, b"other-ad")
    flipped = ct[:-1] + bytes([ct[-1] ^ 1])
    with pytest.raises(InvalidTag):
        P.aead_decrypt(key, flipped, b"ad")
