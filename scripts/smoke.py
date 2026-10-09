"""Run a private dedicated-server startup in an owned, isolated copy."""
import argparse
import configparser
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import time
from tooling import ROOT, configured_directory, require

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--profile", action="store_true", help="Include audited optional mods and updated DeepNorthCompat.")
    modes.add_argument("--skills", action="store_true", help="Include only ImpactfulSkills; validate the migrated bridge without DeepNorthCompat.")
    modes.add_argument("--vcp", action="store_true", help="Include only VCP; validate the migrated takeover without DeepNorthCompat.")
    parser.add_argument("--config-seed", type=Path, help="Load an existing Serverbound config in the private test copy.")
    parser.add_argument("--seconds", type=int, default=180)
    args = parser.parse_args()
    require(30 <= args.seconds <= 600, "Startup deadline must be 30 to 600 seconds.")
    source = configured_directory("SERVERBOUND_SERVER_PATH")
    mods = configured_directory("SERVERBOUND_MODS_PATH")
    runtime = ROOT / "local-audit" / ("smoke-profile" if args.profile else "smoke-skills" if args.skills else "smoke-vcp" if args.vcp else "smoke-standalone")
    require(runtime.resolve().is_relative_to((ROOT / "local-audit").resolve()), "Unsafe runtime path.")
    marker = runtime / ".serverbound-owned"
    require(not runtime.exists() or marker.is_file(), "Refusing to overwrite an unowned runtime.")
    runtime.mkdir(parents=True, exist_ok=True); marker.write_text("Isolated Serverbound test runtime\n")
    for name in ["valheim_server.exe", "UnityPlayer.dll", "steamclient.dll", "steamclient64.dll", "steam_appid.txt"]:
        if (source / name).is_file(): shutil.copy2(source / name, runtime / name)
    for name in ["valheim_server_Data", "MonoBleedingEdge", "D3D12"]:
        if (source / name).is_dir(): shutil.copytree(source / name, runtime / name, dirs_exist_ok=True)
    bepinex = runtime / "BepInEx"
    if bepinex.exists():
        require(bepinex.resolve().is_relative_to(runtime.resolve()), "Unsafe BepInEx path.")
        shutil.rmtree(bepinex)
    for name in ["core", "config"] + (["plugins", "patchers"] if args.profile else []):
        shutil.copytree(mods / "BepInEx" / name, bepinex / name, dirs_exist_ok=True)
    for name in ["winhttp.dll", "doorstop_config.ini", ".doorstop_version"]:
        if (mods / name).is_file(): shutil.copy2(mods / name, runtime / name)
    plugins = bepinex / "plugins"; plugins.mkdir(exist_ok=True)
    # Only the private test copy changes. Remove known predecessor binaries there.
    for filename in ["Serverside_Simulations.dll", "Valheim_Serverside.dll", "SarkasticGG_Dedicated_Simulation.dll", "DeepNorthCompat.dll", "Serverbound.dll"]:
        for old in plugins.rglob(filename): old.unlink()
    target = plugins / "Serverbound"; target.mkdir(exist_ok=True)
    shutil.copy2(ROOT / "bin/Release/Serverbound.dll", target / "Serverbound.dll")
    if args.skills:
        for filename in ("ImpactfulSkills.dll", "Jotunn.dll"):
            matches = list((mods / "BepInEx/plugins").rglob(filename))
            require(len(matches) == 1, "Expected one audited input: " + filename)
            target = plugins / Path(filename).stem; target.mkdir(exist_ok=True)
            shutil.copy2(matches[0], target / filename)
    if args.vcp:
        for filename in ("ValheimCommunityPatch.dll", "Jotunn.dll"):
            matches = list((mods / "BepInEx/plugins").rglob(filename))
            require(len(matches) == 1, "Expected one audited input: " + filename)
            target = plugins / Path(filename).stem; target.mkdir(exist_ok=True)
            shutil.copy2(matches[0], target / filename)
    if args.profile:
        compat = Path(os.environ["SERVERBOUND_COMPAT_PATH"]).resolve()
        target = plugins / "DeepNorthCompat"; target.mkdir(exist_ok=True)
        shutil.copy2(compat, target / "DeepNorthCompat.dll")
    settings = bepinex / "config/BepInEx.cfg"
    cfg = configparser.ConfigParser(interpolation=None); cfg.optionxform = str; cfg.read(settings, encoding="utf-8-sig")
    cfg["Logging.Console"]["Enabled"] = "false"; cfg["Logging.Disk"]["AppendLog"] = "false"
    with settings.open("w", encoding="utf-8") as stream: cfg.write(stream)
    legacy = bepinex / "config/MVP.Valheim_Serverside_Simulations.cfg"
    plugin_settings = bepinex / "config/org.serverbound.valheim.cfg"
    if args.config_seed:
        require(args.config_seed.is_file(), "Config seed does not exist.")
        shutil.copy2(args.config_seed, plugin_settings)
    elif args.profile and legacy.exists(): shutil.copy2(legacy, plugin_settings)
    initial_config_hash = hashlib.sha256(plugin_settings.read_bytes()).hexdigest() if plugin_settings.exists() else None
    private = runtime / "private-state"; private.mkdir(exist_ok=True)
    env = dict(os.environ, SteamAppId="892970")
    for variable, folder in [("APPDATA","roaming"),("LOCALAPPDATA","local"),("USERPROFILE","home")]:
        path = private / folder; path.mkdir(exist_ok=True); env[variable] = str(path)
    command = [str(runtime / "valheim_server.exe"), "-batchmode", "-nographics", "-name", "Serverbound private validation",
               "-world", "ServerboundSmoke", "-password", secrets.token_hex(12), "-port", "26940" if args.profile else "26944" if args.skills else "26946" if args.vcp else "26942",
               "-public", "0", "-savedir", str(private / "saves"), "-logFile", str(runtime / "unity.log")]
    gates = ["Simulation: APPLIED", "Serverbound installed"]
    if args.skills:
        gates += ["OwnerSkills: APPLIED"]
    if args.vcp:
        gates += ["Removed ValheimCommunityPatch's spawn queue", "Removed ValheimCommunityPatch's zone-diff unload"]
    if args.profile:
        gates += ["OwnerSkills: APPLIED", "VPO.Burst: APPLIED", "Removed ValheimCommunityPatch's spawn queue", "Removed ValheimCommunityPatch's zone-diff unload"]
    ready = False
    with (runtime / "stdout.log").open("w",encoding="utf-8") as output:
        process = subprocess.Popen(command, cwd=runtime, env=env, stdin=subprocess.PIPE, stdout=output, stderr=subprocess.STDOUT, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        print(f"Temporary {runtime.name} server PID {process.pid}", flush=True)
        try:
            deadline = time.monotonic() + args.seconds
            while process.poll() is None and time.monotonic() < deadline:
                log = bepinex / "LogOutput.log"
                contents = log.read_text(encoding="utf-8",errors="replace") if log.exists() else ""
                if all(gate in contents for gate in gates) and "Game server connected" in contents:
                    ready = True; process.stdin.write("save\nstop\n"); process.stdin.flush(); break
                if "SERVERBOUND DISABLED" in contents or "Serverbound: REJECTED" in contents:
                    break
                if any("Could not load [" + name in contents for name in
                       ("Serverbound", "ImpactfulSkills" if args.skills else "ValheimCommunityPatch" if args.vcp else "Serverbound")):
                    break
                time.sleep(0.5)
            if not ready and process.poll() is None: process.stdin.write("stop\n"); process.stdin.flush()
            try: process.wait(timeout=30)
            except subprocess.TimeoutExpired: process.terminate(); process.wait(timeout=10)
        finally:
            if process.poll() is None: process.kill(); process.wait(timeout=10)
            if process.stdin: process.stdin.close()
            print(f"Temporary server stopped; exit {process.returncode}", flush=True)
    saved_config = configparser.ConfigParser(interpolation=None)
    saved_config.optionxform = str
    saved_config.read(plugin_settings, encoding="utf-8-sig")
    config_updated = (saved_config.has_option("General", "Enabled")
                      and not any(saved_config.has_section(section) for section in ("CharacterGuard", "ItemLedger")))
    result = {"role": runtime.name, "ready": ready, "exitCode": process.returncode,
              "initialConfigSha256": initial_config_hash, "retiredSettingsRemoved": config_updated,
              "dllSha256": hashlib.sha256((ROOT / "bin/Release/Serverbound.dll").read_bytes()).hexdigest(),
              "runtimeInputs": {path.relative_to(bepinex).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                                for path in sorted(bepinex.rglob("*")) if path.is_file() and path.suffix.lower() in (".dll", ".cfg")}}
    (runtime / "result.json").write_text(json.dumps(result,indent=2))
    require(ready and process.returncode == 0, "Startup gate failed. Inspect isolated logs.")
    require(config_updated, "Serverbound config was not generated or still contains retired settings.")

if __name__ == "__main__": main()
