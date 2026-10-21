"""Build local test ZIP bytes only; never publish or bypass official descriptors."""
import hashlib
import json
import pathlib
import struct
import sys
import zipfile

output, template, worker, version, mode, module_id = sys.argv[1:]
root = pathlib.Path(output)
root.mkdir(parents=True, exist_ok=True)
manifest = json.loads(pathlib.Path(template).read_text())
manifest["Id"] = module_id
manifest["Version"] = version
payloads = {"bin/module-worker": pathlib.Path(worker).read_bytes(),
            "data/boundary.dat": bytes(range(256)) * 1024, "data/empty.txt": b""}
if mode == "migration":
    manifest["Data"] = {"Version": 2, "MinimumReadableVersion": 1, "MaximumReadableVersion": 2}
if mode == "dependency":
    manifest["Dependencies"] = [{"Id": "dropspace.base", "MinimumVersion": "1.0.0", "MaximumVersionExclusive": "2.0.0"}]
if mode == "windows":
    manifest["System"] = "windows"
    manifest["Architecture"] = "x64"
if mode == "traversal":
    payloads["../escaped.txt"] = b"never extract"
if mode == "unicode":
    payloads["data/caf\u00e9.txt"] = b"one"
    payloads["data/cafe\u0301.txt"] = b"two"
if mode == "case":
    payloads["data/BOUNDARY.dat"] = b"duplicate"
if mode == "ancestor":
    payloads["data/boundary.dat/child.txt"] = b"collision"
manifest["Files"] = [{"Path": name, "Bytes": len(value), "Sha256": hashlib.sha256(value).hexdigest()}
                     for name, value in payloads.items()]
if mode == "hash":
    manifest["Files"][0]["Sha256"] = "0" * 64
if mode == "unlisted":
    payloads["data/unlisted.txt"] = b"not declared"
declaration = json.dumps(manifest, ensure_ascii=False).encode()
if mode == "duplicate-json":
    declaration = declaration[:-1] + b',"id":"dropspace.sample"}'
archive = root / "package.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
    bundle.writestr("manifest.json", declaration)
    for name, payload in payloads.items():
        entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_STORED if mode == "stored" else zipfile.ZIP_DEFLATED
        entry.create_system = 3
        entry.external_attr = (0o100600 << 16)
        if mode == "symlink" and name == "data/empty.txt":
            entry.external_attr = 0o120777 << 16
        bundle.writestr(entry, payload)
if mode in ("crc", "expanded"):
    content = bytearray(archive.read_bytes())
    with zipfile.ZipFile(archive) as bundle:
        item = bundle.getinfo("data/boundary.dat")
        local = item.header_offset
        end = content.rfind(b"PK\x05\x06")
        central = struct.unpack_from("<I", content, end + 16)[0]
        while content[central:central + 4] == b"PK\x01\x02":
            name, extra, comment = struct.unpack_from("<HHH", content, central + 28)
            if content[central + 46:central + 46 + name] == b"data/boundary.dat":
                if mode == "crc":
                    struct.pack_into("<I", content, local + 14, item.CRC ^ 1)
                    struct.pack_into("<I", content, central + 16, item.CRC ^ 1)
                else:
                    struct.pack_into("<I", content, local + 22, 513 * 1024 * 1024)
                    struct.pack_into("<I", content, central + 24, 513 * 1024 * 1024)
                break
            central += 46 + name + extra + comment
    archive.write_bytes(content)
data = archive.read_bytes()
package = {"Id": manifest["Id"], "Version": version,
           "Url": f'https://github.com/airanluo-dot/DropSpace/releases/download/macos-dlc-{manifest["Id"]}-{version}/{manifest["Id"]}-{version}-macos-arm64.zip',
           "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()}
(root / "descriptor.json").write_text(json.dumps(package))
