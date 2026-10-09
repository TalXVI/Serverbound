"""Check a downloaded ZIP, its provenance and the complete tagged source snapshot."""
import argparse
import json
from pathlib import Path
import subprocess
import zipfile
from candidate import ROOT, sha, sources

DLL_MEMBER = "BepInEx/plugins/Serverbound/Serverbound.dll"
MEMBERS = sorted(["manifest.json", "README.md", "CHANGELOG.md", "icon.png", DLL_MEMBER])


def verify_archive(path, data, root=ROOT, release_tag=None):
    approved = json.loads((root / "package/validated-build.json").read_text())
    if any(data.get(key) != value for key, value in approved.items()):
        raise RuntimeError("Downloaded provenance differs from the validated source record.")
    files = sources(root)
    if files != approved["sourceFiles"] or sha(json.dumps(files, sort_keys=True).encode()) != approved["sourceSha256"]:
        raise RuntimeError("Source differs from the validated snapshot.")
    if release_tag:
        if release_tag != "v" + approved["version"]:
            raise RuntimeError("Release tag differs from the validated version.")
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        if data.get("testedCommit") != commit:
            raise RuntimeError("Release provenance targets a different commit.")
    if path.name != data["packageFile"] or sha(path.read_bytes()) != data["packageSha256"]:
        raise RuntimeError("Archive differs from the reviewed candidate.")
    with zipfile.ZipFile(path) as archive:
        if archive.namelist() != MEMBERS or archive.testzip():
            raise RuntimeError("Unexpected, corrupt or duplicate ZIP entries.")
        if sha(archive.read(DLL_MEMBER)) != approved["dllSha256"]:
            raise RuntimeError("DLL differs from tested bytes.")
        manifest = json.loads(archive.read("manifest.json"))
        if manifest != json.loads((root / "package/manifest.json").read_text()):
            raise RuntimeError("Packaged manifest differs from tagged sources.")
        if manifest["name"] != "Serverbound" or manifest["version_number"] != approved["version"]:
            raise RuntimeError("Wrong package identity.")
        if manifest["website_url"] != "https://github.com/TalXVI/Serverbound" or manifest["dependencies"] != ["denikson-BepInExPack_Valheim-5.4.2351"]:
            raise RuntimeError("Wrong package website or dependencies.")
        for name in MEMBERS:
            if name == DLL_MEMBER:
                continue
            source = "package/manifest.json" if name == "manifest.json" else name
            if sha(archive.read(name)) != files[source]:
                raise RuntimeError("Packaged source differs: " + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("record", type=Path)
    parser.add_argument("--release-tag")
    args = parser.parse_args()
    verify_archive(args.archive, json.loads(args.record.read_text()), release_tag=args.release_tag)
    print("Downloaded package, provenance and source snapshot match.")


if __name__ == "__main__":
    main()
