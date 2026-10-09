"""Prepare release notes and verify the local candidate without publishing."""
import json
import subprocess
import sys
from candidate import ROOT, verify


def release_changes(changelog, version):
    lines = changelog.splitlines()
    headings = [index for index, line in enumerate(lines) if line.strip() == "## " + version]
    if len(headings) != 1:
        raise RuntimeError("Expected one changelog section for " + version)
    start = headings[0] + 1
    end = next((index for index in range(start, len(lines)) if lines[index].startswith("## ")), len(lines))
    changes = "\n".join(lines[start:end]).strip()
    if not changes:
        raise RuntimeError("Empty changelog section for " + version)
    return changes


def main():
    data = verify()
    dist = ROOT / "dist"
    provenance = json.loads((dist / "candidate.json").read_text())
    archive = dist / provenance["packageFile"]
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/verify-release.py"),
                    str(archive), str(dist / "candidate.json")], cwd=ROOT, check=True)
    changes = release_changes((ROOT / "CHANGELOG.md").read_text(encoding="utf-8"), data["version"])
    notes = f"""{changes}

Remove predecessor simulation DLLs before installing. ImpactfulSkills participants need matching Serverbound on the client and server. If you use DeepNorthCompat, upgrade it to 1.2.0 before adding Serverbound. Follow the [upgrade instructions](https://github.com/TalXVI/Serverbound#upgrade).

The attached ZIP contains the tested DLL. candidate.json records source, dependency and artifact hashes and validation evidence. SHA256SUMS records the package and DLL hashes.
"""
    (dist / "release-notes.md").write_text(notes, encoding="utf-8")
    print("Prepared Serverbound " + data["version"] + " release notes. No external action performed.")


if __name__ == "__main__":
    main()
