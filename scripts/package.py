"""Create a deterministic local candidate ZIP from the validated plugin bytes."""
import json
import re
import struct
import zipfile
import xml.etree.ElementTree as ET
from candidate import ROOT, verify, sha

def main():
    data = verify()
    manifest = json.loads((ROOT / "package/manifest.json").read_text())
    if manifest["name"] != "Serverbound" or manifest["version_number"] != data["version"]:
        raise RuntimeError("Package identity differs from the tested candidate.")
    if ET.parse(ROOT / "src/Serverbound/Serverbound.csproj").findtext("PropertyGroup/Version") != data["version"]:
        raise RuntimeError("Project and package versions differ.")
    if not re.fullmatch(r"[A-Za-z0-9_]{1,128}", manifest["name"]) or len(manifest["description"]) > 250:
        raise RuntimeError("Invalid Thunderstore metadata.")
    if not re.fullmatch(r"\d+\.\d+\.\d+", manifest["version_number"]) or any(not re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", dep) for dep in manifest["dependencies"]):
        raise RuntimeError("Invalid version or dependency.")
    icon = (ROOT / "icon.png").read_bytes()
    if icon[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", icon[16:24]) != (256,256):
        raise RuntimeError("Package icon must be a 256x256 PNG.")
    entries = {name:(ROOT / name).read_bytes() for name in ["README.md", "CHANGELOG.md", "icon.png"]}
    entries["manifest.json"] = (ROOT / "package/manifest.json").read_bytes()
    entries["BepInEx/plugins/Serverbound/Serverbound.dll"] = (ROOT / "bin/Release/Serverbound.dll").read_bytes()
    dist = ROOT / "dist"; dist.mkdir(exist_ok=True)
    target = dist / ("Serverbound-" + data["version"] + ".zip")
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, contents in sorted(entries.items()):
            info = zipfile.ZipInfo(name, (1980,1,1,0,0,0)); info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3; info.external_attr = 0o100644 << 16
            archive.writestr(info, contents, compresslevel=9)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() or sha(archive.read("BepInEx/plugins/Serverbound/Serverbound.dll")) != data["dllSha256"]:
            raise RuntimeError("Packaged DLL differs from the tested DLL.")
    (dist / "SHA256SUMS").write_text(sha(target.read_bytes()) + "  " + target.name + "\n" + data["dllSha256"] + "  Serverbound.dll\n", encoding="utf-8")
    provenance = dict(data, packageFile=target.name, packageSha256=sha(target.read_bytes()))
    (dist / "candidate.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print("Prepared local candidate: " + str(target))

if __name__ == "__main__":
    main()
