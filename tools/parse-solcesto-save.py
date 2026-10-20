#!/usr/bin/env python3
"""解析 SolCesto 存档（Chromium IndexedDB leveldb WAL），提取 localStorage 键值。

已验证的事实（2026-10-21）：
  - 000011.log 是 leveldb WAL，32KB block，每条记录 7 字节头
  - Construct 3 把数据存进 localStorage，实际落在 IndexedDB 的
    c3-localstorage-<id> 数据库里
  - 键值对格式：1字节长度 + UTF-16LE key + 0x12 + varint长度 + varint值
"""
import struct, sys, os

SAVE = (r"C:\Users\11726\AppData\Local\SolCesto\User Data\Default\IndexedDB"
        r"\chrome-extension_anopiimlkmdoenonenclohfilpeenfmj_0.indexeddb.leveldb\000011.log")

BLOCK = 32768


def read_records(path):
    data = open(path, "rb").read()
    pos = 0
    out = []
    while pos < len(data):
        bo = pos % BLOCK
        if bo + 7 > BLOCK:
            pos += BLOCK - bo
            continue
        if pos + 7 > len(data):
            break
        crc, length, rtype = struct.unpack("<IHB", data[pos:pos + 7])
        if length == 0 or rtype == 0 or pos + 7 + length > len(data):
            pos += 1
            continue
        out.append((pos, rtype, data[pos + 7:pos + 7 + length]))
        pos += 7 + length
    return out


def varint(b, i):
    r = 0
    s = 0
    while i < len(b):
        x = b[i]
        i += 1
        r |= (x & 0x7F) << s
        if not (x & 0x80):
            return r, i
        s += 7
    return None, i


def extract(records, keyname):
    """返回 [(offset, value)]，按文件顺序（即写入顺序）。"""
    kb = bytes([len(keyname)]) + keyname.encode("utf-16-le") + b"\x12"
    hits = []
    for off, rtype, pl in records:
        if rtype != 1:
            continue
        idx = 0
        while True:
            i = pl.find(kb, idx)
            if i < 0:
                break
            j = i + len(kb)
            vln, k2 = varint(pl, j)
            if vln is not None and k2 + vln <= len(pl):
                num, _ = varint(pl[k2:k2 + vln], 0)
                hits.append((off, num))
            idx = i + len(kb)
    return hits


def main():
    records = read_records(SAVE)
    lines = [f"文件: {SAVE}", f"大小: {os.path.getsize(SAVE)} 字节",
             f"WAL 记录: {len(records)}", ""]

    for key in ["or", "orEver", "or_ancien", "mendiantLore", "meta"]:
        hits = extract(records, key)
        lines.append(f"=== {key} ({len(hits)} 条) ===")
        for off, val in hits:
            lines.append(f"  @0x{off:>10X} = {val}")
        if hits:
            lines.append(f"  最后 = {hits[-1][1]}")
        lines.append("")

    out = r"F:\git\temp\game-trainer\temp\solcesto-save-parse.txt"
    open(out, "w", encoding="utf-8").write("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
