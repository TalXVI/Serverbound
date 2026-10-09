"""Build Serverbound against local dedicated assemblies and run both Mono roles."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from tooling import ROOT, child_environment, configured_directory, installation_paths, reference_assemblies

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-package", action="store_true")
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--reuse-native-evidence", action="store_true",
                        help="Recheck existing native captures for identical DLL and input bytes instead of starting games.")
    args = parser.parse_args()
    if args.reuse_native_evidence and (args.no_package or args.build_only):
        parser.error("--reuse-native-evidence requires a full build, suite and package")
    mods, client = installation_paths()
    server = configured_directory("SERVERBOUND_SERVER_PATH")
    if not args.build_only:
        configured_directory("SERVERBOUND_VENDOR_PATH")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("Install the SDK pinned in global.json.")
    env = child_environment(mods, server)
    options = ["-p:UseSharedCompilation=false", "-nodeReuse:false", "-p:NuGetAudit=false",
               "-p:RestoreLockedMode=true",
               "-p:TargetFrameworkRootPath=" + reference_assemblies()]
    subprocess.run([dotnet, "build", "Serverbound.sln", "-c", "Release", *options], cwd=ROOT, env=env, check=True)
    if args.build_only:
        return
    env = child_environment(mods, client)
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/test-existing-build.py")], cwd=ROOT, env=env, check=True)
    if args.no_package:
        return
    # Rebuild from clean compiler output to verify deterministic plugin bytes.
    dll = ROOT / "bin/Release/Serverbound.dll"
    first = hashlib.sha256(dll.read_bytes()).hexdigest()
    subprocess.run([dotnet, "clean", "Serverbound.sln", "-c", "Release", *options], cwd=ROOT, env=child_environment(mods, server), check=True, stdout=subprocess.DEVNULL)
    subprocess.run([dotnet, "build", "Serverbound.sln", "-c", "Release", *options], cwd=ROOT, env=child_environment(mods, server), check=True)
    if first != hashlib.sha256(dll.read_bytes()).hexdigest():
        raise RuntimeError("Clean rebuild changed Serverbound.dll; reproducibility gate failed.")
    if os.name != "nt":
        raise RuntimeError("The first candidate requires native Windows startup validation. Linux needs its own acceptance evidence.")
    if not args.reuse_native_evidence:
        for role in ([], ["--skills"], ["--vcp"], ["--profile"]):
            subprocess.run([sys.executable, "-B", str(ROOT / "scripts/smoke.py"), *role], cwd=ROOT, env=child_environment(mods, server), check=True)
    from candidate import record
    record(mods, client, server)
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/test-release.py")], cwd=ROOT, check=True)
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/package.py")], cwd=ROOT, check=True)

if __name__ == "__main__":
    main()
