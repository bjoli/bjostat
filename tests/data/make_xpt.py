# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

"""Writes tests/data/small.xpt, the SAS transport file of tests/xpt.bjo.

    python3 tests/data/make_xpt.py

Written by hand from SAS's description of the format (TS-140), not by SAS,
so that the test does not need R or pandas. The numbers are the IBM floats
spelled out in bytes, each checked by hand below.

Member TEST, four rows:

    ID  X       SHORT  NAME   D           DT
    1   1       1      ab     1960-01-01  1960-01-01T00:00
    2   -118.625 0.5   (blank) 2020-01-01  1970-01-01T00:00:01.5
    3   .       100    é      .           .
    4   .A      -1     abcdef 1959-12-31  .

SHORT is a number of 4 bytes, D has the format DATE9., DT DATETIME20.

Member LETTERS, one text variable of 10 bytes and the rows "a", (blank),
"c": 30 bytes, padded to 80 with 50 blanks, five rows of blanks that are
the padding and not rows.
"""

import struct
import sys
from pathlib import Path

# IBM floats, 8 bytes. 1 is 0.0625 * 16^1: exponent 65 (0x41), fraction
# 0x10... -118.625 is SAS's own example, C2 76 A0. 0.5 is 0.5 * 16^0:
# 0x40, fraction 0x80. 100 is 0.390625 * 16^2 = 0x64 / 256 * 256: 0x42 64.
# 21915 (days from 1960-01-01 to 2020-01-01) is 0x559B: 0.0101100110011011
# * 16^4, 0x44 55 9B. 315619201.5 seconds (1970-01-01T00:00:01.5) is
# 0x12CF_F781.8: 0x48 12 CF F7 81 80.
IBM = {
    0: bytes(8),
    1: bytes.fromhex("4110000000000000"),
    2: bytes.fromhex("4120000000000000"),
    3: bytes.fromhex("4130000000000000"),
    4: bytes.fromhex("4140000000000000"),
    -1: bytes.fromhex("C110000000000000"),
    0.5: bytes.fromhex("4080000000000000"),
    100: bytes.fromhex("4264000000000000"),
    -118.625: bytes.fromhex("C276A00000000000"),
    21915: bytes.fromhex("44559B0000000000"),
    315619201.5: bytes.fromhex("4812CFF781800000"),
    ".": b"." + bytes(7),
    ".A": b"A" + bytes(7),
}


def record(text: bytes) -> bytes:
    assert len(text) <= 80
    return text.ljust(80, b" ")


def header(kind: str, tail: str = "0" * 30) -> bytes:
    return record(f"HEADER RECORD*******{kind:<8}HEADER RECORD!!!!!!!{tail}".encode("ascii"))


def padded(data: bytes) -> bytes:
    return data + b" " * (-len(data) % 80)


def namestr(number: bool, length: int, index: int, name: str, label: str,
            fmt: str, position: int) -> bytes:
    d = struct.pack(">hhhh", 1 if number else 2, 0, length, index)
    d += name.encode("latin-1").ljust(8)
    d += label.encode("latin-1").ljust(40)
    d += fmt.encode("ascii").ljust(8)
    d += struct.pack(">hhh", 9 if fmt else 0, 0, 0) + bytes(2)
    d += b" " * 8 + struct.pack(">hh", 0, 0)
    d += struct.pack(">i", position)
    d += bytes(52)
    assert len(d) == 140
    return d


def member(name: str, label: str, variables, rows) -> bytes:
    out = header("MEMBER", "00000000000000000160000000" + "0140")
    out += header("DSCRPTR")
    out += record(b"SAS     " + name.encode().ljust(8) + b"SASDATA 9.4     LINUX   " + b" " * 24
                  + b"02NOV26:12:00:00")
    out += record(b"02NOV26:12:00:00" + b" " * 16 + label.encode().ljust(40) + b" " * 8)
    out += header("NAMESTR", f"000000{len(variables):04d}" + "0" * 20)
    position = 0
    descriptors = b""
    for i, (vname, vlabel, number, length, fmt) in enumerate(variables):
        descriptors += namestr(number, length, i + 1, vname, vlabel, fmt, position)
        position += length
    out += padded(descriptors)
    out += header("OBS")
    data = b""
    for row in rows:
        for (vname, _, number, length, _), value in zip(variables, row):
            cell = IBM[value][:length] if number else value.encode("latin-1").ljust(length)
            assert len(cell) == length
            data += cell
    return out + padded(data)


def main() -> None:
    lib = header("LIBRARY")
    lib += record(b"SAS     SAS     SASLIB  9.4     LINUX   " + b" " * 24 + b"02NOV26:12:00:00")
    lib += record(b"02NOV26:12:00:00")

    test = member(
        "TEST", "Values of every kind",
        [("ID", "Row number", True, 8, ""),
         ("X", "A number", True, 8, ""),
         ("SHORT", "A number of 4 bytes", True, 4, ""),
         ("NAME", "Some text", False, 6, ""),
         ("D", "A date", True, 8, "DATE"),
         ("DT", "A datetime", True, 8, "DATETIME")],
        [(1, 1, 1, "ab", 0, 0),
         (2, -118.625, 0.5, "", 21915, 315619201.5),
         (3, ".", 100, "é", ".", "."),
         (4, ".A", -1, "abcdef", -1, ".")])

    letters = member("LETTERS", "", [("C", "A letter", False, 10, "")],
                     [("a",), ("",), ("c",)])

    out = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).with_name("small.xpt"))
    out.write_bytes(lib + test + letters)


if __name__ == "__main__":
    main()
