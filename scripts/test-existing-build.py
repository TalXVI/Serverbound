"""Run the offline suite in the locally installed Unity Mono runtime."""

import ctypes
import os
from pathlib import Path
import sys
import subprocess
import re

from tooling import ROOT, TEST_EXECUTABLE, child_environment, installation_paths, managed_path


def mono_library(game):
    override = os.environ.get("SERVERBOUND_MONO_LIBRARY")
    if override:
        path = Path(override).expanduser().resolve()
        if not path.is_file():
            raise FileNotFoundError(f"SERVERBOUND_MONO_LIBRARY does not exist: {path}")
        return path
    names = {
        "win32": ("mono-2.0-bdwgc.dll",),
        "linux": ("libmonobdwgc-2.0.so", "libmono-2.0-bdwgc.so"),
        "darwin": ("libmonobdwgc-2.0.dylib", "libmono-2.0-bdwgc.dylib"),
    }
    if sys.platform not in names:
        raise RuntimeError("Set SERVERBOUND_MONO_LIBRARY for this platform.")
    folder = game / "MonoBleedingEdge/EmbedRuntime"
    matches = [folder / name for name in names[sys.platform] if (folder / name).is_file()]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one Unity Mono library under {folder}; set SERVERBOUND_MONO_LIBRARY explicitly.")
    return matches[0]


def run_suite(runtime, game, executable):
    def bind(name, result, *arguments):
        function = getattr(runtime, name)
        function.restype = result
        function.argtypes = list(arguments)
        return function

    pointer, string, integer = ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int
    set_dirs = bind("mono_set_dirs", None, string, string)
    set_assemblies = bind("mono_set_assemblies_path", None, string)
    config_parse = bind("mono_config_parse", None, string)
    initialize = bind("mono_jit_init_version", pointer, string, string)
    register = bind("mono_add_internal_call", None, string, pointer)
    open_assembly = bind("mono_domain_assembly_open", pointer, pointer, string)
    execute = bind("mono_jit_exec", integer, pointer, pointer, integer, ctypes.POINTER(string))
    cleanup = bind("mono_jit_cleanup", None, pointer)
    managed = str(managed_path(game)).encode("utf-8")
    set_dirs(managed, str(game / "MonoBleedingEdge/etc").encode("utf-8"))
    set_assemblies(managed)
    config_parse(None)
    domain = initialize(b"ServerboundOfflineValidation", b"v4.0.30319")
    if not domain:
        raise RuntimeError("Game Mono runtime did not initialize.")
    try:
        def quaternion_identity(_euler, result):
            values = ctypes.cast(result, ctypes.POINTER(ctypes.c_float))
            for index, value in enumerate((0, 0, 0, 1)):
                values[index] = value

        def vector_zero(result):
            values = ctypes.cast(result, ctypes.POINTER(ctypes.c_float))
            for index in range(3):
                values[index] = 0

        def vector2_zero(result):
            values = ctypes.cast(result, ctypes.POINTER(ctypes.c_float))
            values[0] = values[1] = 0

        # These are the same native scene boundaries as the original offline host.
        # Keep every callback alive until Mono finishes. No Unity scene is created.
        callbacks = [
            ("UnityEngine.Application::get_platform", ctypes.CFUNCTYPE(integer)(lambda: 44 if os.environ.get("SERVERBOUND_TEST_MODE") == "server" else 2)),
            ("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", ctypes.CFUNCTYPE(integer)(lambda: 0)),
            ("UnityEngine.Object::CurrentThreadIsMainThread", ctypes.CFUNCTYPE(integer)(lambda: 1)),
            ("UnityEngine.Animator::StringToHash_Injected", ctypes.CFUNCTYPE(integer, pointer)(lambda span: 0)),
            # VisEquipment's static initializer resolves shader property IDs when Harmony
            # patches its method. The preview tests inspect hooks without a Unity renderer.
            ("UnityEngine.Shader::PropertyToID_Injected", ctypes.CFUNCTYPE(integer, pointer)(lambda span: 0)),
            ("UnityEngine.Random::get_value", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0.5)),
            ("UnityEngine.Random::Range", ctypes.CFUNCTYPE(ctypes.c_float, ctypes.c_float, ctypes.c_float)(lambda low, high: (low + high) * 0.5)),
            ("UnityEngine.Random::RandomRangeInt", ctypes.CFUNCTYPE(integer, integer, integer)(lambda low, high: (low + high - 1) // 2 if high > low else low)),
            ("UnityEngine.Quaternion::Internal_FromEulerRad_Injected", ctypes.CFUNCTYPE(None, pointer, pointer)(quaternion_identity)),
            ("UnityEngine.Random::get_insideUnitSphere_Injected", ctypes.CFUNCTYPE(None, pointer)(vector_zero)),
            ("UnityEngine.Random::GetRandomUnitCircle", ctypes.CFUNCTYPE(None, pointer)(vector2_zero)),
            ("UnityEngine.Component::get_gameObject_Injected", ctypes.CFUNCTYPE(pointer, pointer)(lambda component: None)),
            ("UnityEngine.Component::get_transform_Injected", ctypes.CFUNCTYPE(pointer, pointer)(lambda component: None)),
            ("UnityEngine.Behaviour::set_enabled_Injected", ctypes.CFUNCTYPE(None, pointer, integer)(lambda component, enabled: None)),
            ("UnityEngine.Transform::get_position_Injected", ctypes.CFUNCTYPE(None, pointer, pointer)(lambda transform, result: vector_zero(result))),
            ("UnityEngine.Transform::get_rotation_Injected", ctypes.CFUNCTYPE(None, pointer, pointer)(quaternion_identity)),
            ("UnityEngine.Quaternion::Internal_ToEulerRad_Injected", ctypes.CFUNCTYPE(None, pointer, pointer)(lambda rotation, result: vector_zero(result))),
            ("UnityEngine.Quaternion::LookRotation_Injected", ctypes.CFUNCTYPE(None, pointer, pointer, pointer)(lambda forward, up, result: quaternion_identity(None, result))),
            ("UnityEngine.Random::get_rotation_Injected", ctypes.CFUNCTYPE(None, pointer)(lambda result: quaternion_identity(None, result))),
            ("UnityEngine.Time::get_frameCount", ctypes.CFUNCTYPE(integer)(lambda: 1)),
            ("UnityEngine.Time::get_fixedTime", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0)),
            ("UnityEngine.Time::get_deltaTime", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 1 / 60)),
            ("UnityEngine.Time::get_unscaledTime", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0)),
            ("UnityEngine.Time::get_time", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0)),
            ("UnityEngine.Time::get_fixedDeltaTime", ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0.02)),
            ("UnityEngine.LayerMask::NameToLayer_Injected", ctypes.CFUNCTYPE(integer, pointer)(lambda span: 0)),
            ("UnityEngine.Object::Destroy_Injected", ctypes.CFUNCTYPE(None, pointer, ctypes.c_float)(lambda value, delay: None)),
        ]
        for name, callback in callbacks:
            register(name.encode("ascii"), ctypes.cast(callback, pointer))
        test = str(executable).encode("utf-8")
        assembly = open_assembly(domain, test)
        if not assembly:
            raise RuntimeError("Mono could not open offline tests.")
        argv = (string * 2)(test, None)
        return execute(domain, assembly, 1, argv)
    finally:
        cleanup(domain)


def main():
    if not os.environ.get("SERVERBOUND_TEST_MODE"):
        lab, game = installation_paths()
        cases = [("client", game)]
        if os.environ.get("SERVERBOUND_SERVER_PATH"):
            cases.append(("server", Path(os.environ["SERVERBOUND_SERVER_PATH"]).resolve()))
        passed = 0
        counts = {}
        for mode, installation in cases:
            environment = child_environment(lab, installation)
            environment["SERVERBOUND_TEST_MODE"] = mode
            suite = subprocess.Popen([sys.executable, "-B", str(Path(__file__).resolve())], cwd=ROOT,
                                     env=environment, stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
            count = None
            for line in suite.stdout:
                print(line, end="", flush=True)
                match = re.fullmatch(r"PASS: (\d+) test cases", line.strip())
                if match:
                    count = int(match.group(1))
            if suite.wait() != 0:
                return suite.returncode
            if count is None:
                raise RuntimeError(f"The {mode} suite did not report its pass count.")
            passed += count
            counts[mode] = count
        import json
        evidence = ROOT / "local-audit/test-results.json"
        evidence.parent.mkdir(exist_ok=True)
        evidence.write_text(json.dumps(counts))
        print(f"PASS: {passed} test cases")
        return 0
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        raise RuntimeError("Use 64-bit Python to load Valheim's 64-bit Mono runtime.")
    if not TEST_EXECUTABLE.is_file():
        raise FileNotFoundError("Build the offline tests with scripts/build-and-test.py first.")
    lab, game = installation_paths()
    os.environ.update(child_environment(lab, game))
    library = mono_library(game)
    dll_directory = os.add_dll_directory(str(library.parent)) if os.name == "nt" else None
    try:
        runtime = ctypes.CDLL(str(library), mode=ctypes.RTLD_GLOBAL)
        return run_suite(runtime, game, TEST_EXECUTABLE)
    finally:
        if dll_directory is not None:
            dll_directory.close()


if __name__ == "__main__":
    sys.exit(main())
