"""Protocol property tests: the project's evaluation plan written as code.

Each test is skipped until its milestone lands. Un-skip a test as soon as the code
it covers exists. Section 4 of the report reports these results.
"""

import pytest

todo = pytest.mark.skip(reason="not implemented yet")

# ---------------- X3DH (milestone 1)


@todo
def test_x3dh_both_sides_derive_same_sk_with_opk(): ...


@todo
def test_x3dh_both_sides_derive_same_sk_without_opk(): ...


@todo
def test_x3dh_rejects_bad_signed_prekey_signature(): ...


@todo
def test_x3dh_one_time_prekey_deleted_after_use(): ...


@todo
def test_x3dh_cross_check_against_reference_impl():
    """Interop/known-answer check: fix all private keys, then compare SK with an
    independent implementation (e.g. the python-x3dh package, or a hand-run script)."""


# ---------------- Double Ratchet (milestone 2)


@todo
def test_ratchet_ping_pong_1000_messages(): ...


@todo
def test_ratchet_out_of_order_delivery():
    """Shuffle a batch across several DH ratchet steps. Everything must decrypt."""


@todo
def test_ratchet_dropped_messages_then_resume(): ...


@todo
def test_ratchet_max_skip_enforced(): ...


@todo
def test_ratchet_tampered_ciphertext_or_header_rejected_and_state_unchanged(): ...


@todo
def test_ratchet_replay_rejected(): ...


@todo
def test_ratchet_message_keys_never_reused(): ...


# ---------------- Security properties (milestone 4)


@todo
def test_forward_secrecy_state_snapshot_cannot_decrypt_past():
    """Snapshot Bob's state at time t. Using only the snapshot, attempt to
    decrypt messages captured before t. Every attempt must fail."""


@todo
def test_post_compromise_security_heals_after_one_round_trip():
    """Attacker copies Alice's state at t. Show it can decrypt until the next
    DH ratchet step, and that once Alice and Bob each send once, it can no longer decrypt."""


@todo
def test_server_never_observes_plaintext():
    """Run a full conversation through the real server. Grep its DB and logs for the plaintexts."""
