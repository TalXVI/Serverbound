"""Hash the reviewed source and local inputs without recording private paths."""
import hashlib
import json
import os
import platform
import subprocess
import zlib
from pathlib import Path
from tooling import ROOT, managed_path

RECORD = ROOT / "package/validated-build.json"
def sha(data):
    return hashlib.sha256(data).hexdigest()

def sources(root=ROOT):
    names = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root).decode().split("\0")
    files = {}
    for name in sorted(set(names)):
        path = root / name
        if not name or name == "package/validated-build.json" or not path.is_file():
            continue
        files[name] = sha(path.read_bytes())
    return files

def record(mods, client, server):
    files = sources()
    inputs = {}
    for role, game in [("client", client), ("server", server)]:
        for path in sorted(managed_path(game).glob("*.dll")):
            inputs[role + "/Managed/" + path.name] = sha(path.read_bytes())
        runtime = game / "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"
        inputs[role + "/Mono/mono-2.0-bdwgc.dll"] = sha(runtime.read_bytes())
    for filename in ["BepInEx.dll", "0Harmony.dll", "Mono.Cecil.dll", "MonoMod.Utils.dll"]:
        inputs[filename] = sha((mods / "BepInEx/core" / filename).read_bytes())
    for filename in ["ImpactfulSkills.dll", "ValheimPerformanceOptimizations.dll", "ValheimCommunityPatch.dll", "Jotunn.dll"]:
        matches = list((mods / "BepInEx/plugins").rglob(filename))
        if len(matches) != 1:
            raise RuntimeError("Expected exactly one audited test input: " + filename)
        inputs[filename] = sha(matches[0].read_bytes())
    inputs["ValheimTune.dll"] = sha((Path(os.environ["SERVERBOUND_VENDOR_PATH"]) / "tune.dll").read_bytes())
    inputs["DeepNorthCompat.dll"] = sha(Path(os.environ["SERVERBOUND_COMPAT_PATH"]).read_bytes())
    compat_root = Path(os.environ.get("SERVERBOUND_COMPAT_REPO", ROOT.parent / "DeepNorthCompat"))
    compat_record = json.loads((compat_root / "package/validated-build.json").read_text())
    if compat_record["sha256"] != inputs["DeepNorthCompat.dll"]:
        raise RuntimeError("DeepNorthCompat candidate differs from its validated package record.")
    compat_names = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=compat_root).decode().split("\0")
    compat_files = {name: sha((compat_root / name).read_bytes()) for name in sorted(set(compat_names))
                    if name and name != "package/validated-build.json" and (compat_root / name).is_file()}
    if compat_record["sourceFiles"] != compat_files:
        raise RuntimeError("DeepNorthCompat source changed after its full-suite validation.")
    evidence = json.loads((ROOT / "local-audit/test-results.json").read_text())
    dll_hash = sha((ROOT / "bin/Release/Serverbound.dll").read_bytes())
    startup = []
    for role in ("smoke-standalone", "smoke-skills", "smoke-vcp", "smoke-profile"):
        runtime = ROOT / "local-audit" / role
        result = json.loads((runtime / "result.json").read_text())
        if not result["ready"] or result["exitCode"] != 0 or result["dllSha256"] != dll_hash:
            raise RuntimeError("Refresh native startup for the exact candidate: " + role)
        if sha((runtime / "BepInEx/plugins/Serverbound/Serverbound.dll").read_bytes()) != dll_hash:
            raise RuntimeError("Native startup captured a different Serverbound DLL: " + role)
        for path in sorted(managed_path(server).glob("*.dll")):
            captured = managed_path(runtime) / path.name
            if not captured.is_file() or sha(captured.read_bytes()) != inputs["server/Managed/" + path.name]:
                raise RuntimeError("Native startup used different game inputs: " + role + "/" + path.name)
        if sha((runtime / "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll").read_bytes()) != inputs["server/Mono/mono-2.0-bdwgc.dll"]:
            raise RuntimeError("Native startup used a different Mono runtime: " + role)
        for filename in ("BepInEx.dll", "0Harmony.dll", "Mono.Cecil.dll", "MonoMod.Utils.dll"):
            if sha((runtime / "BepInEx/core" / filename).read_bytes()) != inputs[filename]:
                raise RuntimeError("Native startup used different loader inputs: " + role + "/" + filename)
        vendors = {"smoke-skills": ("ImpactfulSkills.dll", "Jotunn.dll"),
                   "smoke-vcp": ("ValheimCommunityPatch.dll", "Jotunn.dll"),
                   "smoke-profile": ("ImpactfulSkills.dll", "ValheimPerformanceOptimizations.dll",
                                     "ValheimCommunityPatch.dll", "Jotunn.dll", "DeepNorthCompat.dll")}
        for filename in vendors.get(role, ()):
            captured = list((runtime / "BepInEx/plugins").rglob(filename))
            if len(captured) != 1 or sha(captured[0].read_bytes()) != inputs[filename]:
                raise RuntimeError("Native startup used different optional inputs: " + role + "/" + filename)
        result["logSha256"] = sha((runtime / "BepInEx/LogOutput.log").read_bytes())
        startup.append(result)
    manifest = json.loads((ROOT / "package/manifest.json").read_text())
    data = {"pluginGUID": "org.serverbound.valheim", "version": manifest["version_number"],
            "upstreamCommit": "92591fd02b6aee19b31689479ae886be28771dfa",
            "sdk": subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip(),
            "packagingRuntime": {"python": platform.python_version(), "zlib": zlib.ZLIB_RUNTIME_VERSION},
            "sourceFiles": files, "sourceSha256": sha(json.dumps(files, sort_keys=True).encode()),
            "dllSha256": dll_hash,
            "inputs": inputs, "offlineTests": evidence, "cleanRebuildIdentical": True,
            "coordinatedDeepNorthCompat": compat_record,
            "nativeStartup": startup, "multiplayerAccepted": False, "performanceMeasured": False,
            "releaseScope": "Initial 0.1.0 release with incomplete multiplayer and performance acceptance",
            "followUp": ["Multiplayer acceptance matrix", "Extended multiplayer and optional-mod performance acceptance"]}
    gameplay = ROOT / "package/native-gameplay.json"
    if gameplay.is_file():
        data["nativeGameplay"] = json.loads(gameplay.read_text())
        if data["nativeGameplay"]["dllSha256"] != dll_hash:
            raise RuntimeError("Native gameplay evidence targets a different DLL. Refresh it or remove obsolete evidence before claiming a new candidate.")
    performance = ROOT / "package/native-performance.json"
    if performance.is_file():
        data["nativePerformance"] = json.loads(performance.read_text())
        if data["nativePerformance"]["dllSha256"] != dll_hash:
            raise RuntimeError("Native performance evidence targets a different DLL. Refresh it or remove obsolete evidence before claiming a new candidate.")
        if any(inputs.get(name) != value for name, value in data["nativePerformance"]["nativeInputs"].items()):
            raise RuntimeError("Native performance evidence used different game/Mono inputs.")
        data["performanceMeasured"] = True
        data["performanceAccepted"] = data["nativePerformance"]["performanceAccepted"]
    RECORD.parent.mkdir(exist_ok=True)
    RECORD.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")

def verify():
    data = json.loads(RECORD.read_text())
    if sources() != data["sourceFiles"]:
        raise RuntimeError("Source changed since validation. Rebuild and rerun the suite.")
    if sha((ROOT / "bin/Release/Serverbound.dll").read_bytes()) != data["dllSha256"]:
        raise RuntimeError("DLL changed since validation.")
    return data
