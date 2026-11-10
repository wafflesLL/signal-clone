import pytest
from cryptography.exceptions import InvalidTag
from e2ee import x3dh as X, ratchet as R, primitives as P

def setup(opks=1):
    a, b = X.generate_identity(), X.generate_identity()
    store, bundles = X.generate_prekeys(b, opks)
    bundle = bundles[0]
    ska, ada, ek = X.initiate(a, bundle)
    initial = X.InitialMessage(P.sig_pub_bytes(a.sig_priv), P.dh_pub_bytes(a.dh_priv), P.dh_pub_bytes(ek), 1, bundle.one_time_prekey_id, b'')
    skb, adb = X.respond(store, initial)
    assert ska == skb and ada == adb
    return a, store, bundle, initial, R.init_alice(ska, bundle.signed_prekey_pub), R.init_bob(skb, store.signed_prekeys[1]), ada

@pytest.mark.parametrize('opks', [0, 1])
def test_x3dh(opks):
    setup(opks)

def test_bad_signature():
    a = X.generate_identity()
    _, bundles = X.generate_prekeys(X.generate_identity())
    bundles[0].signed_prekey_sig = b'\x00'*64
    with pytest.raises(ValueError): X.initiate(a, bundles[0])

def test_opk_consumption():
    _, store, _, msg, *_ = setup()
    X.consume_one_time_prekey(store, msg.one_time_prekey_id)
    with pytest.raises(ValueError): X.respond(store, msg)

def test_pingpong_and_replay():
    *_, a, b, ad = setup()
    for i in range(100):
        sender, receiver = (a,b) if i%2==0 else (b,a)
        header, ct = R.encrypt(sender, f'message {i}'.encode(), ad)
        assert R.decrypt(receiver, header, ct, ad) == f'message {i}'.encode()
        with pytest.raises(ValueError): R.decrypt(receiver, header, ct, ad)

def test_out_of_order_and_tamper_rollback():
    *_, a, b, ad = setup()
    messages = [R.encrypt(a, str(i).encode(), ad) for i in range(5)]
    h, ct = messages[4]
    with pytest.raises(InvalidTag): R.decrypt(b, h, ct[:-1] + bytes([ct[-1]^1]), ad)
    assert b.DHr is None
    for i in [4,0,2,1,3]:
        h, ct = messages[i]
        assert R.decrypt(b, h, ct, ad) == str(i).encode()

def test_max_skip():
    *_, a, b, ad = setup()
    h, ct = R.encrypt(a, b'hi', ad)
    h.n = R.MAX_SKIP + 1
    with pytest.raises(ValueError): R.decrypt(b, h, ct, ad)
