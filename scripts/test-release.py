"""Exercise publication guards without game assemblies, credentials or remote writes."""
import copy
import json
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
import zipfile
from candidate import ROOT, sha, sources

validation = runpy.run_path(str(ROOT / "scripts/verify-release.py"))
verify_archive = validation["verify_archive"]
DLL_MEMBER = validation["DLL_MEMBER"]


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        audit = ROOT / "local-audit"
        audit.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="release-test-", dir=audit)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "--quiet")
        self.git("config", "user.name", "Release fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.write(".gitignore", b"dist/\n")
        self.write("source.cs", b"class Fixture {}\n")
        self.payload = {member: ("fixture " + member).encode() for member in validation["MEMBERS"]}
        self.payload[DLL_MEMBER] = b"tested plugin fixture"
        manifest = {"name": "Serverbound", "version_number": "0.1.0", "description": "Fixture",
                    "website_url": "https://github.com/TalXVI/Serverbound",
                    "dependencies": ["denikson-BepInExPack_Valheim-5.4.2351"]}
        self.payload["manifest.json"] = json.dumps(manifest).encode()
        for name, content in self.payload.items():
            if name != DLL_MEMBER:
                self.write("package/manifest.json" if name == "manifest.json" else name, content)
        files = sources(self.root)
        self.approved = {"version": "0.1.0", "sourceFiles": files,
                         "sourceSha256": sha(json.dumps(files, sort_keys=True).encode()),
                         "dllSha256": sha(self.payload[DLL_MEMBER])}
        self.write("package/validated-build.json", json.dumps(self.approved).encode())
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Create fixture")
        self.archive = self.root / "dist/Serverbound-0.1.0.zip"
        self.archive.parent.mkdir()
        self.data = dict(self.approved, packageFile=self.archive.name, testedCommit=self.git("rev-parse", "HEAD"))
        self.pack()

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def pack(self):
        with zipfile.ZipFile(self.archive, "w") as archive:
            for name, data in sorted(self.payload.items()):
                archive.writestr(name, data)
        self.data["packageSha256"] = sha(self.archive.read_bytes())

    def verify(self, data=None, tag="v0.1.0"):
        verify_archive(self.archive, self.data if data is None else data, self.root, tag)

    def test_valid_release_and_committed_record(self):
        self.verify()
        self.assertEqual(sources(self.root), self.approved["sourceFiles"])

    def test_source_change_outside_package_rejected(self):
        self.write("source.cs", b"class Changed {}\n")
        with self.assertRaisesRegex(RuntimeError, "Source differs"):
            self.verify()

    def test_wrong_tag_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "tag differs"):
            self.verify(tag="v0.2.0")

    def test_wrong_commit_rejected(self):
        changed = dict(self.data, testedCommit="0" * 40)
        with self.assertRaisesRegex(RuntimeError, "different commit"):
            self.verify(changed)

    def test_substituted_dll_rejected_even_with_updated_zip_hash(self):
        self.payload[DLL_MEMBER] = b"substituted plugin"
        self.pack()
        with self.assertRaisesRegex(RuntimeError, "DLL differs"):
            self.verify()

    def test_unexpected_payload_rejected(self):
        self.payload["vendor.dll"] = b"unexpected"
        self.pack()
        with self.assertRaisesRegex(RuntimeError, "ZIP entries"):
            self.verify()

    def test_missing_required_readme_rejected(self):
        del self.payload["README.md"]
        self.pack()
        with self.assertRaisesRegex(RuntimeError, "ZIP entries"):
            self.verify()

    def test_changed_provenance_rejected(self):
        changed = copy.deepcopy(self.data)
        changed["dllSha256"] = "0" * 64
        with self.assertRaisesRegex(RuntimeError, "provenance differs"):
            self.verify(changed)

    def test_package_document_change_rejected(self):
        self.payload["README.md"] = b"stale documentation"
        self.pack()
        with self.assertRaisesRegex(RuntimeError, "Packaged source differs"):
            self.verify()


if __name__ == "__main__":
    record = json.loads((ROOT / "package/validated-build.json").read_text())
    files = sources()
    if files != record["sourceFiles"] or sha(json.dumps(files, sort_keys=True).encode()) != record["sourceSha256"]:
        raise RuntimeError("Repository source differs from the validated snapshot.")
    unittest.main()
