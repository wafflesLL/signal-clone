"""Educational Double Ratchet, following Signal's basic DH/chain ratchet."""
from dataclasses import dataclass, field
from . import primitives as P

MAX_SKIP = 1000

@dataclass
class Header:
    dh_pub: bytes
    pn: int
    n: int
    def encode(self):
        if len(self.dh_pub) != 32 or min(self.pn, self.n) < 0 or max(self.pn, self.n) >= 2**32:
            raise ValueError('invalid header')
        return self.dh_pub + self.pn.to_bytes(4, 'big') + self.n.to_bytes(4, 'big')
    @classmethod
    def decode(cls, data):
        if len(data) != 40:
            raise ValueError('header must be 40 bytes')
        return cls(data[:32], int.from_bytes(data[32:36], 'big'), int.from_bytes(data[36:40], 'big'))

@dataclass
class RatchetState:
    DHs: object
    DHr: bytes | None
    RK: bytes
    CKs: bytes | None
    CKr: bytes | None
    Ns: int = 0
    Nr: int = 0
    PN: int = 0
    MKSKIPPED: dict[tuple[bytes, int], bytes] = field(default_factory=dict)

def init_alice(sk, bob_dh_pub):
    dhs = P.dh_generate()
    rk, cks = P.kdf_rk(sk, P.dh(dhs, P.dh_pub_from_bytes(bob_dh_pub)))
    return RatchetState(dhs, bob_dh_pub, rk, cks, None)

def init_bob(sk, bob_dh_keypair):
    return RatchetState(bob_dh_keypair, None, sk, None, None)

def encrypt(state, plaintext, ad):
    if state.CKs is None:
        raise ValueError('no sending chain yet')
    ck, mk = P.kdf_ck(state.CKs)
    header = Header(P.dh_pub_bytes(state.DHs), state.PN, state.Ns)
    ct = P.aead_encrypt(mk, plaintext, ad + header.encode())
    state.CKs, state.Ns = ck, state.Ns + 1
    return header, ct

def _skip(state, until):
    if until < state.Nr or until - state.Nr > MAX_SKIP or len(state.MKSKIPPED) + until - state.Nr > MAX_SKIP:
        raise ValueError('invalid or excessive skipped messages')
    if state.CKr is None:
        if until != state.Nr:
            raise ValueError('missing receiving chain')
        return
    while state.Nr < until:
        state.CKr, mk = P.kdf_ck(state.CKr)
        state.MKSKIPPED[(state.DHr, state.Nr)] = mk
        state.Nr += 1

def _dh_ratchet(state, new_pub):
    state.PN, state.Ns, state.Nr = state.Ns, 0, 0
    state.DHr = new_pub
    state.RK, state.CKr = P.kdf_rk(state.RK, P.dh(state.DHs, P.dh_pub_from_bytes(new_pub)))
    state.DHs = P.dh_generate()
    state.RK, state.CKs = P.kdf_rk(state.RK, P.dh(state.DHs, P.dh_pub_from_bytes(new_pub)))

def decrypt(state, header, ciphertext, ad):
    working = RatchetState(state.DHs, state.DHr, state.RK, state.CKs, state.CKr, state.Ns, state.Nr, state.PN, state.MKSKIPPED.copy())
    encoded = header.encode()
    key_id = (header.dh_pub, header.n)
    if key_id in working.MKSKIPPED:
        mk = working.MKSKIPPED.pop(key_id)
        pt = P.aead_decrypt(mk, ciphertext, ad + encoded)
    else:
        if header.dh_pub != working.DHr:
            _skip(working, header.pn)
            _dh_ratchet(working, header.dh_pub)
        if header.n < working.Nr:
            raise ValueError('replayed or expired message')
        _skip(working, header.n)
        if working.CKr is None:
            raise ValueError('no receiving chain')
        working.CKr, mk = P.kdf_ck(working.CKr)
        working.Nr += 1
        pt = P.aead_decrypt(mk, ciphertext, ad + encoded)
    state.__dict__.update(working.__dict__)
    return pt
