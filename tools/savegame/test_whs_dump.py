#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""WO-174 Phase 0.1 -- tests for whs_dump.py.

A synthetic container always exercises the field save's 148-chunk layout.
The original is checked as well when KCDMP_FIELD_SAVE is set or logs/saves/autosave335.whs exists.
Run: python -I tools/savegame/test_whs_dump.py
"""
import hashlib
import os
import struct
import sys
import tempfile
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import whs_dump as w  # noqa: E402


def build(body_chunks, xml=b'<C_SaveGameDescription SaveId="7" LevelName="trosecko"/>'):
    xml_blob = xml + b'\n\x00'
    out = bytearray(b'\xff\xff\xff\xff' + struct.pack('<I', len(xml_blob)) + xml_blob)
    for raw in body_chunks:
        z = zlib.compress(raw)
        out += struct.pack('<II', len(z), len(raw)) + z
    foot = b'0XBP' + bytes(16) + bytes(44)
    md5 = hashlib.md5(bytes(out) + foot).digest()
    return bytes(out) + b'0XBP' + md5 + bytes(44)


class Synthetic(unittest.TestCase):
    def test_parse_sizes_and_body(self):
        chunks = [b'A' * 0x8000, b'kcd2mp_2' + b'B' * 100, b'']
        s = w.parse(build(chunks))
        self.assertEqual(len(s['chunks']), 3)
        self.assertEqual([r for _, r in s['chunks']], [0x8000, 108, 0])
        self.assertEqual(s['body'], b''.join(chunks))
        self.assertTrue(s['footer_ok'])

    def test_stored_chunk(self):
        out = bytearray(b'\xff\xff\xff\xff' + struct.pack('<I', 2) + b'\n\x00')
        raw = bytes(range(200))
        out += struct.pack('<iI', -1, len(raw)) + raw
        foot = b'0XBP' + bytes(16) + bytes(44)
        data = bytes(out) + b'0XBP' + hashlib.md5(bytes(out) + foot).digest() + bytes(44)
        s = w.parse(data)
        self.assertEqual(s['body'], raw)
        self.assertTrue(s['footer_ok'])

    def test_footer_detects_a_flipped_byte(self):
        data = bytearray(build([b'x' * 500]))
        data[20] ^= 1
        self.assertFalse(w.parse(bytes(data))['footer_ok'])

    def test_rejects_oversize_chunk_and_bad_magic(self):
        bad = bytearray(build([b'x' * 10]))
        bad[0] = 0
        with self.assertRaises(ValueError):
            w.parse(bytes(bad))
        z = zlib.compress(b'y' * 0x9000)
        data = b'\xff\xff\xff\xff' + struct.pack('<I', 2) + b'\n\x00' + struct.pack('<II', len(z), 0x9000) + z + bytes(64)
        with self.assertRaises(ValueError):
            w.parse(data)

    def test_occurrences_and_names_near(self):
        body = b'\x00ttac_man_9\x00kcd2mp_2\x0025425\x00Dude\x00kcd2mp_2'
        self.assertEqual(len(w.occurrences(body, 'kcd2mp_2')), 2)
        names = [n for _, n in w.names_near(body, body.find(b'kcd2mp_2'), 40)]
        self.assertIn('Dude', ' '.join(names))

    def test_desc_fields_never_returns_the_machine(self):
        xml = '<d SaveId="9" LevelName="trosecko" DebugInfoHistory="user=SOMEONE machine=PC1"/>'
        f = w.desc_fields(xml)
        self.assertEqual(f['SaveId'], '9')
        self.assertNotIn('DebugInfoHistory', f)
        self.assertNotIn('SOMEONE', ' '.join(f.values()))


class LargeSave(unittest.TestCase):
    def test_148_chunks_sizes_match_and_footer(self):
        chunks = [bytes([index]) * 0x8000 for index in range(147)] + [b'\x93' * 7815]
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'large-synthetic.whs')
            with open(path, 'wb') as fixture:
                fixture.write(build(chunks))
            self.check_large_save(path)

        original = os.environ.get('KCDMP_FIELD_SAVE')
        fallback = os.path.join(HERE, '..', '..', 'logs', 'saves', 'autosave335.whs')
        if original:
            self.check_large_save(original)
        elif os.path.isfile(fallback):
            self.check_large_save(fallback)

    def check_large_save(self, path):
        s = w.load(path)
        self.assertEqual(len(s['chunks']), 148)
        self.assertEqual(sum(r for _, r in s['chunks']), len(s['body']))
        self.assertEqual(len(s['body']), 4824711)
        self.assertTrue(s['footer_ok'])


if __name__ == '__main__':
    unittest.main()
