"""Educational X3DH variant with separate Ed25519 signing identity."""
from dataclasses import dataclass, field
from . import primitives as P

INFO = b'COSC483-E2EE-X3DH'
F = b'\xff' * 32

@dataclass
class IdentityKeys:
    sig_priv: object
    dh_priv: object

@dataclass
class PrekeyBundle:
    identity_sig_pub: bytes
    identity_dh_pub: bytes
    signed_prekey_id: int
    signed_prekey_pub: bytes
    signed_prekey_sig: bytes
    one_time_prekey_id: int | None = None
    one_time_prekey_pub: bytes | None = None

@dataclass
class LocalPrekeyStore:
    identity: IdentityKeys
    signed_prekeys: dict[int, object] = field(default_factory=dict)
    one_time_prekeys: dict[int, object] = field(default_factory=dict)

@dataclass
class InitialMessage:
    identity_sig_pub: bytes
    identity_dh_pub: bytes
    ephemeral_pub: bytes
    signed_prekey_id: int
    one_time_prekey_id: int | None
    ciphertext: bytes

def generate_identity():
    return IdentityKeys(P.sig_generate(), P.dh_generate())

def _signed_data(spk_id, pub):
    return b'COSC483-SPK-v1' + spk_id.to_bytes(4, 'big') + pub

def generate_prekeys(identity, n_one_time=20):
    if n_one_time < 0:
        raise ValueError('negative count')
    spk = P.dh_generate()
    store = LocalPrekeyStore(identity, {1: spk})
    sig = P.sign(identity.sig_priv, _signed_data(1, P.dh_pub_bytes(spk)))
    base = dict(identity_sig_pub=P.sig_pub_bytes(identity.sig_priv), identity_dh_pub=P.dh_pub_bytes(identity.dh_priv), signed_prekey_id=1, signed_prekey_pub=P.dh_pub_bytes(spk), signed_prekey_sig=sig)
    bundles = []
    for i in range(n_one_time):
        opk = P.dh_generate()
        store.one_time_prekeys[i + 1] = opk
        bundles.append(PrekeyBundle(**base, one_time_prekey_id=i + 1, one_time_prekey_pub=P.dh_pub_bytes(opk)))
    if not bundles:
        bundles.append(PrekeyBundle(**base))
    return store, bundles

def _ad(a_sig, a_dh, b_sig, b_dh):
    return b'COSC483-AD-v1' + a_sig + a_dh + b_sig + b_dh

def initiate(alice, bundle):
    if (bundle.one_time_prekey_id is None) != (bundle.one_time_prekey_pub is None):
        raise ValueError('inconsistent OPK bundle')
    if not P.verify(P.sig_pub_from_bytes(bundle.identity_sig_pub), bundle.signed_prekey_sig, _signed_data(bundle.signed_prekey_id, bundle.signed_prekey_pub)):
        raise ValueError('invalid signed prekey signature')
    ek = P.dh_generate()
    spk = P.dh_pub_from_bytes(bundle.signed_prekey_pub)
    parts = [P.dh(alice.dh_priv, spk), P.dh(ek, P.dh_pub_from_bytes(bundle.identity_dh_pub)), P.dh(ek, spk)]
    if bundle.one_time_prekey_pub is not None:
        parts.append(P.dh(ek, P.dh_pub_from_bytes(bundle.one_time_prekey_pub)))
    sk = P.hkdf(F + b''.join(parts), 32, None, INFO)
    ad = _ad(P.sig_pub_bytes(alice.sig_priv), P.dh_pub_bytes(alice.dh_priv), bundle.identity_sig_pub, bundle.identity_dh_pub)
    return sk, ad, ek

def respond(bob, msg):
    if msg.signed_prekey_id not in bob.signed_prekeys:
        raise ValueError('unknown signed prekey')
    if msg.one_time_prekey_id is not None and msg.one_time_prekey_id not in bob.one_time_prekeys:
        raise ValueError('missing or consumed one-time prekey')
    spk = bob.signed_prekeys[msg.signed_prekey_id]
    ik_a = P.dh_pub_from_bytes(msg.identity_dh_pub)
    ek_a = P.dh_pub_from_bytes(msg.ephemeral_pub)
    parts = [P.dh(spk, ik_a), P.dh(bob.identity.dh_priv, ek_a), P.dh(spk, ek_a)]
    if msg.one_time_prekey_id is not None:
        parts.append(P.dh(bob.one_time_prekeys[msg.one_time_prekey_id], ek_a))
    sk = P.hkdf(F + b''.join(parts), 32, None, INFO)
    ad = _ad(msg.identity_sig_pub, msg.identity_dh_pub, P.sig_pub_bytes(bob.identity.sig_priv), P.dh_pub_bytes(bob.identity.dh_priv))
    # Only consume OPK after the first message has been authenticated by the caller.
    return sk, ad

def consume_one_time_prekey(bob, opk_id):
    if opk_id is not None:
        del bob.one_time_prekeys[opk_id]
