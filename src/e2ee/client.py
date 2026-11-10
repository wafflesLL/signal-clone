"""In-memory two-party E2EE demonstration through a running relay.

Run: python -m e2ee.client --server http://127.0.0.1:8000
This demonstration does not persist keys or support independent chat terminals.
"""
import argparse
import base64
import httpx
from . import x3dh as X, ratchet as R, primitives as P

def b64(x): return base64.b64encode(x).decode()
def un64(x): return base64.b64decode(x, validate=True)

def run(base):
    alice, bob = X.generate_identity(), X.generate_identity()
    store, bundles = X.generate_prekeys(bob, 3)
    bundle = bundles[0]
    with httpx.Client(base_url=base, timeout=10) as http:
        def check(resp):
            resp.raise_for_status()
            return resp.json()
        check(http.post('/users/bob/bundle', json={'data': {
            'identity_sig_pub': b64(bundle.identity_sig_pub),
            'identity_dh_pub': b64(bundle.identity_dh_pub),
            'signed_prekey_id': bundle.signed_prekey_id,
            'signed_prekey_pub': b64(bundle.signed_prekey_pub),
            'signed_prekey_sig': b64(bundle.signed_prekey_sig),
            'opks': [{'id': i, 'pub': b64(P.dh_pub_bytes(k))} for i,k in store.one_time_prekeys.items()],
        }}))
        data = check(http.get('/users/bob/bundle'))['data']
        opk = data['opk']
        fetched = X.PrekeyBundle(un64(data['identity_sig_pub']), un64(data['identity_dh_pub']), data['signed_prekey_id'], un64(data['signed_prekey_pub']), un64(data['signed_prekey_sig']), opk['id'] if opk else None, un64(opk['pub']) if opk else None)
        sk_a, ad, ek = X.initiate(alice, fetched)
        ra = R.init_alice(sk_a, fetched.signed_prekey_pub)
        first = X.InitialMessage(P.sig_pub_bytes(alice.sig_priv), P.dh_pub_bytes(alice.dh_priv), P.dh_pub_bytes(ek), fetched.signed_prekey_id, fetched.one_time_prekey_id, b'')
        header, ct = R.encrypt(ra, b'Hello Bob! This is encrypted.', ad)
        check(http.post('/users/bob/messages', json={'data': {
            'initial': {'identity_sig_pub':b64(first.identity_sig_pub), 'identity_dh_pub':b64(first.identity_dh_pub), 'ephemeral_pub':b64(first.ephemeral_pub), 'signed_prekey_id':first.signed_prekey_id, 'one_time_prekey_id':first.one_time_prekey_id},
            'header': b64(header.encode()), 'ciphertext': b64(ct),
        }}))
        envelope = check(http.get('/users/bob/messages'))['messages'][0]
        init = envelope['initial']
        received_first = X.InitialMessage(un64(init['identity_sig_pub']), un64(init['identity_dh_pub']), un64(init['ephemeral_pub']), init['signed_prekey_id'], init['one_time_prekey_id'], b'')
        sk_b, ad_b = X.respond(store, received_first)
        rb = R.init_bob(sk_b, store.signed_prekeys[received_first.signed_prekey_id])
        text = R.decrypt(rb, R.Header.decode(un64(envelope['header'])), un64(envelope['ciphertext']), ad_b)
        X.consume_one_time_prekey(store, received_first.one_time_prekey_id)
        print('Bob decrypted:', text.decode())
        header2, ct2 = R.encrypt(rb, b'Hi Alice, I can read you!', ad_b)
        check(http.post('/users/alice/messages', json={'data': {'header':b64(header2.encode()), 'ciphertext':b64(ct2)}}))
        reply = check(http.get('/users/alice/messages'))['messages'][0]
        print('Alice decrypted:', R.decrypt(ra, R.Header.decode(un64(reply['header'])), un64(reply['ciphertext']), ad).decode())
        print('SUCCESS: relay transported ciphertext; both peers decrypted.')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--server', default='http://127.0.0.1:8000')
    args = parser.parse_args()
    run(args.server)
