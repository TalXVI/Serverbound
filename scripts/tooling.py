"""Shared paths and child-process environment for the offline tools."""

import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEST_EXECUTABLE = ROOT / "tests/Serverbound.Tests/bin/Release/net48/Serverbound.Tests.exe"


def require(condition, message):
    """Fail even when Python runs with -O, which strips assert statements."""
    if not condition:
        raise SystemExit("FAIL: " + message)


def configured_directory(variable, default=None):
    value = os.environ.get(variable) or default
    if not value:
        raise RuntimeError(f"Set {variable} to your local installation directory.")
    path = Path(value).expanduser().resolve()
    if not path.is_dir():
        raise FileNotFoundError(f"{variable} directory does not exist: {path}")
    return path


def installation_paths():
    return (configured_directory("SERVERBOUND_MODS_PATH"),
            configured_directory("SERVERBOUND_VALHEIM_PATH"))


def child_environment(lab, game):
    return dict(os.environ, SERVERBOUND_MODS_PATH=str(lab),
                SERVERBOUND_VALHEIM_PATH=str(game), SERVERBOUND_MANAGED_PATH=str(managed_path(game)), PYTHONDONTWRITEBYTECODE="1",
                DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
                DOTNET_CLI_HOME=str(ROOT / ".dotnet-home"), MSBUILDDISABLENODEREUSE="1",
                DOTNET_CLI_USE_MSBUILD_SERVER="0")


def managed_path(game):
    candidates = [game / name / "Managed" for name in ("valheim_Data", "valheim_server_Data")]
    existing = [p for p in candidates if (p / "assembly_valheim.dll").is_file()]
    if len(existing) != 1:
        raise RuntimeError(f"Expected one client or dedicated-server managed directory under {game}.")
    return existing[0]


def reference_assemblies():
    package_cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    default = package_cache / "microsoft.netframework.referenceassemblies.net48/1.0.3/build"
    root = configured_directory("SERVERBOUND_REFERENCE_ASSEMBLIES_PATH", default)
    if not (root / ".NETFramework/v4.8/mscorlib.dll").is_file():
        raise FileNotFoundError(f"Missing .NET Framework 4.8 reference assemblies under {root}")
    return str(root) + os.sep
