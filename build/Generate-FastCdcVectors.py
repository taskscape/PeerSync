"""Independent stdlib-only FastCDC wire-constant fixtures; no production code imported."""
import hashlib
import json
import struct
from pathlib import Path

MASK64 = (1 << 64) - 1
GEAR = [int.from_bytes(hashlib.sha256(b"psp1 gear" + bytes(6) + bytes([i])).digest()[:8], "little")
        for i in range(256)]
LENGTH = 16 * 1024 * 1024 + 17
data = b"".join(hashlib.sha256(b"psp1 chunk fixture" + struct.pack("<Q", i)).digest()
                for i in range((LENGTH + 31) // 32))[:LENGTH]


def lengths(corpus, bits):
    minimum, average, maximum = 1 << (bits - 2), 1 << bits, 1 << (bits + 2)
    before_mask = MASK64 ^ ((1 << (64 - bits - 2)) - 1)
    after_mask = MASK64 ^ ((1 << (64 - bits + 2)) - 1)
    offset, result = 0, []
    while offset < len(corpus):
        end = min(len(corpus), offset + maximum)
        cut = min(end, offset + minimum)
        fingerprint = 0
        # A separate absolute-position reference, not the streaming window code.
        for position in range(cut, end):
            fingerprint = ((fingerprint * 2) + GEAR[corpus[position]]) & MASK64
            mask = before_mask if position < offset + average else after_mask
            if fingerprint & mask == 0:
                cut = position + 1
                break
        else:
            cut = end
        result.append(cut - offset)
        offset = cut
    return result


result = dict(fixtureOnly=True, generator="Python hashlib + absolute-position uint64 reference",
              corpus="SHA256(ASCII 'psp1 chunk fixture' || uint64-LE block index), concatenated then truncated",
              length=LENGTH, sha256=hashlib.sha256(data).hexdigest(),
              gearSha256=hashlib.sha256(b"".join(struct.pack("<Q", g) for g in GEAR)).hexdigest(),
              vectors=[dict(chunkClass=i, lengths=lengths(data, bits)) for i, bits in enumerate((16, 19, 21))])
target = Path(__file__).resolve().parent.parent / "tests/Fixtures/fastcdc-vectors.json"
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print("Prepared independent golden boundaries for all three PSP/1 chunk classes.")
