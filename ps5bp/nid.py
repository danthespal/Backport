"""NIDs: computation, symbol-name parsing and the aerolib.csv name database."""
import base64
import hashlib
import struct

NID_SUFFIX = bytes.fromhex('518D64A635DED8C1E6B039B1C3E55230')
NID_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-'


def compute_nid(name):
    """11-character NID of a symbol name (SHA-1 with suffix, first 8 bytes reversed, custom base64)."""
    value = struct.unpack('<Q', hashlib.sha1(name.encode() + NID_SUFFIX).digest()[:8])[0]
    return base64.b64encode(struct.pack('>Q', value), b'+-').decode().rstrip('=')


def id_from_chars(chars):
    """Library/module ids in symbol names are base-64 digits: 's' = 44, 'BM' = 76."""
    n = 0
    for c in chars:
        n = n * 64 + NID_ALPHABET.index(c)
    return n


def split_symbol(sym):
    """'<NID>#<lib>#<mod>' -> (nid, lib_id, mod_id), or None for a non-NID name."""
    parts = sym.split('#')
    if len(parts) != 3 or len(parts[0]) != 11:
        return None
    return parts[0], id_from_chars(parts[1]), id_from_chars(parts[2])


class NameDb:
    """NID -> name lookup from aerolib.csv ('<NID> <name>' per line)."""

    def __init__(self, path=None):
        self.names = {}
        if path:
            with open(path, encoding='utf-8', errors='replace') as f:
                for line in f:
                    p = line.split(None, 1)
                    if len(p) == 2:
                        self.names[p[0]] = p[1].strip()

    def name(self, nid):
        return self.names.get(nid, nid)
