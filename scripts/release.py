"""Revalidate pushed main and create a GitHub release that publishes to Thunderstore."""
import argparse
import json
import shutil
import subprocess
import sys
from candidate import ROOT
from tooling import require


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--draft", action="store_true", help="Upload a draft without triggering publication.")
    parser.add_argument("--reuse-native-evidence", action="store_true",
                        help="Require exact existing native captures instead of starting games.")
    args = parser.parse_args()
    gh = shutil.which("gh")
    require(gh, "Install gh and sign in with gh auth login")
    require(not git("status", "--porcelain"), "Commit every source change before releasing")
    require(git("branch", "--show-current") == "main", "Release from main")
    git("fetch", "--quiet", "origin", "main")
    commit = git("rev-parse", "HEAD")
    require(commit == git("rev-parse", "origin/main"), "Push main before releasing")
    version = json.loads((ROOT / "package/manifest.json").read_text())["version_number"]
    tag = "v" + version
    remote_tag = git("ls-remote", "--tags", "origin", "refs/tags/" + tag)
    if remote_tag:
        git("fetch", "--quiet", "origin", "refs/tags/" + tag)
        require(git("rev-parse", "FETCH_HEAD^{commit}") == commit, tag + " targets a different commit")
    existing = subprocess.run([gh, "release", "view", tag, "--json", "tagName"], cwd=ROOT,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    require(existing.returncode != 0, tag + " already has a GitHub release")
    command = [sys.executable, "-B", str(ROOT / "scripts/build-and-test.py")]
    if args.reuse_native_evidence:
        command.append("--reuse-native-evidence")
    subprocess.run(command, cwd=ROOT, check=True)
    require(not git("status", "--porcelain"), "Validation changed the committed record; review, commit and push it")
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/prepare-release.py")], cwd=ROOT, check=True)
    dist = ROOT / "dist"
    provenance = json.loads((dist / "candidate.json").read_text())
    provenance["testedCommit"] = commit
    (dist / "candidate.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    archive = dist / provenance["packageFile"]
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/verify-release.py"), str(archive),
                    str(dist / "candidate.json"), "--release-tag", tag], cwd=ROOT, check=True)
    if not remote_tag:
        local_tag = subprocess.run(["git", "show-ref", "--verify", "--quiet", "refs/tags/" + tag], cwd=ROOT)
        if local_tag.returncode == 0:
            require(git("rev-parse", tag + "^{commit}") == commit, "Local tag targets a different commit")
        else:
            git("tag", "-a", tag, commit, "-m", "Serverbound " + version)
        git("push", "origin", "refs/tags/" + tag)
    command = [gh, "release", "create", tag, str(archive), str(dist / "candidate.json"), str(dist / "SHA256SUMS"),
               "--verify-tag", "--title", "Serverbound " + version, "--notes-file", str(dist / "release-notes.md")]
    if args.draft:
        command.append("--draft")
    subprocess.run(command, cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
