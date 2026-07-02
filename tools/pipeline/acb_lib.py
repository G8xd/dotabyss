"""Minimal CRI @UTF table parser + ACB cue-name resolver."""

import struct


def _u8(d, o):
    return d[o]


def _u16(d, o):
    return struct.unpack_from(">H", d, o)[0]


def _u32(d, o):
    return struct.unpack_from(">I", d, o)[0]


_FMT = {
    0: (">B", 1),
    1: (">b", 1),
    2: (">H", 2),
    3: (">h", 2),
    4: (">I", 4),
    5: (">i", 4),
    6: (">Q", 8),
    7: (">q", 8),
    8: (">f", 4),
    9: (">d", 8),
}


def parse_utf(d, off=0):
    """Return (rows, table_name). rows = list of dict(colname->value).
    String columns -> str; data columns -> bytes."""
    assert d[off : off + 4] == b"@UTF", "not @UTF"
    base = off + 8
    rows_off = _u16(d, off + 0x0A) + base
    string_off = _u32(d, off + 0x0C) + base
    data_off = _u32(d, off + 0x10) + base
    name_off = _u32(d, off + 0x14)
    ncols = _u16(d, off + 0x18)
    row_width = _u16(d, off + 0x1A)
    nrows = _u32(d, off + 0x1C)

    def getstr(rel):
        o = string_off + rel
        e = d.index(b"\x00", o)
        return d[o:e].decode("utf-8", "replace")

    # schema
    cols = []
    p = off + 0x20
    for _ in range(ncols):
        flags = d[p]
        p += 1
        typ = flags & 0x0F
        storage = flags & 0xF0
        cname = getstr(_u32(d, p))
        p += 4
        const = None
        if storage == 0x30:  # constant value inline
            if typ in (0xA,):  # string
                const = getstr(_u32(d, p))
                p += 4
            elif typ == 0xB:  # data
                const = (_u32(d, p), _u32(d, p + 4))
                p += 8
            else:
                fmt, sz = _FMT[typ]
                const = struct.unpack_from(fmt, d, p)[0]
                p += sz
        cols.append((cname, typ, storage, const))
    # rows
    out = []
    for r in range(nrows):
        o = rows_off + r * row_width
        row = {}
        for cname, typ, storage, const in cols:
            if storage == 0x30:
                v = const
            elif storage == 0x10:
                v = 0
            else:  # per-row 0x50
                if typ == 0xA:
                    v = getstr(_u32(d, o))
                    o += 4
                elif typ == 0xB:
                    doff = _u32(d, o)
                    dsz = _u32(d, o + 4)
                    o += 8
                    v = d[data_off + doff : data_off + doff + dsz]
                else:
                    fmt, sz = _FMT[typ]
                    v = struct.unpack_from(fmt, d, o)[0]
                    o += sz
            if typ == 0xB and storage == 0x30 and isinstance(v, tuple):
                v = d[data_off + v[0] : data_off + v[0] + v[1]]
            row[cname] = v
        out.append(row)
    return out, getstr(name_off)


def cue_names(acb_bytes):
    """Return dict cue_index -> cue_name from an ACB's CueNameTable."""
    top, _ = parse_utf(acb_bytes, 0)
    if not top:
        return {}
    blob = top[0].get("CueNameTable")
    if not isinstance(blob, (bytes, bytearray)) or blob[:4] != b"@UTF":
        return {}
    rows, _ = parse_utf(blob, 0)
    return {r["CueIndex"]: r["CueName"] for r in rows if "CueIndex" in r and "CueName" in r}
