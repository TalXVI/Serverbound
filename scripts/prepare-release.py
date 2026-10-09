"""Prepare release notes and verify the local candidate without publishing."""
import json
import subprocess
import sys
from candidate import ROOT, verify


def main():
    data = verify()
    dist = ROOT / "dist"
    provenance = json.loads((dist / "candidate.json").read_text())
    archive = dist / provenance["packageFile"]
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/verify-release.py"),
                    str(archive), str(dist / "candidate.json")], cwd=ROOT, check=True)
    notes = """Serverbound moves world and AI simulation to the dedicated server. Basic simulation accepts vanilla clients.

- Adds VPO/VCP object-management takeover and the ImpactfulSkills client/server bridge.
- Fixes late VCP detection and restores vendor hooks on takeover failure.
- Preserves upstream raid, ready-peer, ship and dungeon fixes.
- Uses a separate assembly, plugin identity and configuration.

Remove predecessor simulation DLLs before installing. ImpactfulSkills participants need matching Serverbound on the client and server. If you use DeepNorthCompat, upgrade it to 1.2.0 before adding Serverbound. Follow the [upgrade instructions](https://github.com/TalXVI/Serverbound#upgrade).

The attached ZIP contains the tested DLL. candidate.json records source, dependency and artifact hashes and validation evidence. SHA256SUMS records the package and DLL hashes.
"""
    (dist / "release-notes.md").write_text(notes, encoding="utf-8")
    print("Prepared Serverbound " + data["version"] + " release notes. No external action performed.")


if __name__ == "__main__":
    main()
