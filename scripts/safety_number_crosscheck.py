#!/usr/bin/env python3
"""Independent implementation of our safety-number scheme, written from the description in
src/E2ee.Core/SafetyNumber.cs without looking at the C# code paths. It prints the value for
the fixed keys used in tests/E2ee.Tests/SafetyNumberTests.cs, so the test checks the C#
against something other than itself.

Run: python3 scripts/safety_number_crosscheck.py
"""
import hashlib

VERSION = bytes([0x00, 0xC4])
ITERATIONS = 5200


def fingerprint(username: str, sig_pub: bytes, dh_pub: bytes) -> str:
    key = sig_pub + dh_pub
    h = hashlib.sha512(VERSION + key + username.encode()).digest()
    for _ in range(ITERATIONS - 1):
        h = hashlib.sha512(h + key).digest()
    return "".join(f"{int.from_bytes(h[i * 5:(i + 1) * 5], 'big') % 100000:05d}" for i in range(6))


def safety_number(a, b) -> str:
    fa, fb = fingerprint(*a), fingerprint(*b)
    combined = fa + fb if fa <= fb else fb + fa
    return " ".join(combined[i * 5:(i + 1) * 5] for i in range(12))


alice = ("alice", bytes(range(32)), bytes(range(32, 64)))
bob = ("bob", bytes([0xAA] * 32), bytes([0x55] * 32))
print(safety_number(alice, bob))
