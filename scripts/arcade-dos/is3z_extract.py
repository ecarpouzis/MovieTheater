"""List/extract an InstallShield 3 "Z" library (signature 13 5D 65 8C). Knowledge Adventure ships them as
.KAL; other mid-90s installers as .Z/.LIB. Only STORED entries are supported (all of Magic Theatre's are);
a compressed one fails loudly rather than writing garbage.
Usage: is3z_extract.py <archive> [outdir]   (no outdir = list only)"""
import struct, sys, os

def parse(d):
    assert struct.unpack_from('<I', d, 0)[0] == 0x8C655D13, 'not an IS3 Z archive'
    nfiles = struct.unpack_from('<H', d, 0x0C)[0]
    dir_off, = struct.unpack_from('<I', d, 0x29)
    ndirs, = struct.unpack_from('<H', d, 0x31)
    file_off, = struct.unpack_from('<I', d, 0x33)
    dirs, o = [], dir_off
    for _ in range(ndirs):
        cnt, esz, nlen = struct.unpack_from('<HHH', d, o)
        dirs.append(d[o+6:o+6+nlen].decode('ascii'))
        o += esz
    files, o = [], file_off
    for _ in range(nfiles):
        di, osz, csz, off = struct.unpack_from('<HIII', d, o + 1)
        esz, = struct.unpack_from('<H', d, o + 0x17)
        nlen = d[o + 0x1D]
        name = d[o+0x1E:o+0x1E+nlen].decode('ascii')
        files.append((dirs[di], name, osz, csz, off))
        o += esz
    return files

d = open(sys.argv[1], 'rb').read()
files = parse(d)
out = sys.argv[2] if len(sys.argv) > 2 else None
stored = sum(1 for f in files if f[2] == f[3])
print(f'{len(files)} files, {stored} stored uncompressed')
for dname, name, osz, csz, off in files:
    rel = os.path.join(dname, name) if dname else name
    blob = d[off:off+csz]
    if out is None:
        print(f'{rel:40} {osz:9} {csz:9} {off:#x} {blob[:2]!r}')
        continue
    if osz != csz:
        sys.exit(f'{rel}: compressed entry ({csz} -> {osz}); decompressor not implemented')
    p = os.path.join(out, rel)
    os.makedirs(os.path.dirname(p) or out, exist_ok=True)
    open(p, 'wb').write(blob)
if out: print('extracted to', out)
