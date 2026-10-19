"""Regenerate public synthetic fixtures (optional: pip install cryptography==50.0.2).

The normal .NET test run reads the checked-in JSON and does not need Python.
No application secrets are read. Run from any working directory.
"""
import base64
import hashlib
import hmac
import json
from pathlib import Path

from cryptography import __version__
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat

ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"


def derive(secret, info):
    prk = hmac.new(bytes(32), secret, hashlib.sha256).digest()
    return hmac.new(prk, info + b"\x01", hashlib.sha256).digest()


def crc10(data):
    # Independent reference: polynomial long division of data*x^10 by G(x).
    value = int.from_bytes(data, "big") << 10
    generator = 0x633  # x^10 + x^9 + x^5 + x^4 + x + 1
    while value.bit_length() > 10:
        value ^= generator << (value.bit_length() - 11)
    return value


def encode(prefix, payload):
    check = crc10(prefix.encode("ascii") + payload)
    return (prefix + base64.b32encode(payload).decode("ascii").rstrip("=")
            + ALPHABET[check >> 5] + ALPHABET[check & 31])


assert crc10(b"123456789") == 0x199
message = b"PeerSync public fixture, not a production folder key"
vectors = []
for name, rw in (("zero", bytes(32)), ("ascending", bytes(range(32))),
                 ("descending", bytes(reversed(range(224, 256))))):
    seed = derive(rw, b"psp1 sign")
    private = Ed25519PrivateKey.from_private_bytes(seed)
    pk = private.public_key().public_bytes(Encoding.Raw, PublicFormat.Raw)
    content = derive(rw, b"psp1 content")
    vectors.append(dict(name=name, readWrite=rw.hex(), signingSeed=seed.hex(),
                        publicKey=pk.hex(), contentKey=content.hex(),
                        authKey=derive(pk, b"psp1 auth").hex(),
                        readOnlyAuthKey=derive(content, b"psp1 ro-auth").hex(),
                        folderId=hashlib.sha256(b"psp1 folder" + pk).hexdigest(),
                        message=message.hex(), signature=private.sign(message).hex(),
                        readWriteText=encode("A", rw),
                        readOnlyText=encode("B", pk + content),
                        encryptedText=encode("F", pk)))
result = dict(fixtureOnly=True, generator=f"Python hmac/hashlib + PyCA cryptography {__version__}",
              checksum="CRC-10/ATM, poly 0x233, init/xorout 0, no reflection, prefix ASCII + payload",
              vectors=vectors)
target = Path(__file__).resolve().parent.parent / "tests/Fixtures/folder-key-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(f"Prepared {len(vectors)} independent public fixture vectors; no key material printed.")
