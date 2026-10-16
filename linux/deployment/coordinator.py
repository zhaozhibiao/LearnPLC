#!/usr/bin/env python3
"""Private-instance staged deployment coordinator. Never calls native upload."""
import base64
import contextlib
import hashlib
import json
import os
import pathlib
import re
import shutil
import signal
import socket
import stat
import subprocess
import sys
import threading
import time
import uuid
import zipfile
import ssl
import shlex
import urllib.request
import motion_cycle
import axis_gate
import communication_gate

RUNTIME_COMMIT = "a9e38d901fb87deb5a8cf778b52639b9387834d0"
MAX_ARCHIVE = 32 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
MAX_LOG = 1024 * 1024
REQUIRED_EXPORTS = {"strucpp_get_config", "strucpp_get_located_vars", "strucpp_get_located_var_count",
    "strucpp_advance_time", "strucpp_set_current_time", "strucpp_program_md5", "strucpp_debug_array_count",
    "strucpp_debug_elem_count", "strucpp_debug_size", "strucpp_debug_set", "strucpp_debug_read", "strucpp_debug_write"}


class DeploymentError(Exception):
    pass


def digest(path):
    checksum = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            checksum.update(chunk)
    return checksum.hexdigest()

def durable_copy(source, destination):
    shutil.copy2(source, destination)
    with open(destination, "r+b") as stream:
        os.fsync(stream.fileno())
    sync_directory(pathlib.Path(destination).parent)

def canonical_pinned_source(relative, content):
    content = content.replace(b"\r\n", b"\n")
    if relative == "core/src/plc_app/task_policy.h":
        isolated = b'/home/a/.local/share/st-workbench/runtime-v4.3.0/run/watchdog_fault'
        if content.count(isolated) == 1:
            return content.replace(isolated, b'/run/runtime/watchdog_fault')
    return content


def identity(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", value):
        raise DeploymentError("Invalid build identity")
    return value.upper()


def validate_archive(path, package_sha256, build_hash):
    build_hash = identity(build_hash)
    path = pathlib.Path(path)
    if path.stat().st_size > MAX_ARCHIVE or digest(path).lower() != package_sha256.lower():
        raise DeploymentError("Package size or transport checksum mismatch")
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if not entries or len(entries) > 1024 or sum(entry.file_size for entry in entries) > MAX_EXPANDED:
            raise DeploymentError("Archive capacity exceeded")
        names = set()
        for entry in entries:
            name = entry.filename
            parts = pathlib.PurePosixPath(name).parts
            if (not parts or name.startswith("/") or "\\" in name or ":" in name or any(part in ("..", ".") for part in parts)
                    or str(pathlib.PurePosixPath(name)) != name or name in names or stat.S_ISLNK(entry.external_attr >> 16) or entry.is_dir()
                    or entry.file_size > 16 * 1024 * 1024 or entry.file_size > max(1, entry.compress_size) * 500):
                raise DeploymentError("Unsafe archive member: " + name)
            if not (name in {"LICENSE", "COPYING", "COPYING.RUNTIME", "RUNTIME-COMPAT-LICENSE", "control/LICENSE", "paths/LICENSE", "paths/virtual_worker.py", "runtime_lifecycle.inc", "axis_lifecycle.inc", "axes/axis_worker.py", "axes/LICENSE"} or name in communication_gate.FILES or pathlib.PurePosixPath(name).suffix in {".cpp", ".hpp", ".h", ".st", ".json"}):
                raise DeploymentError("Unexpected archive file: " + name)
            names.add(name)
        if "build-manifest.json" not in names:
            raise DeploymentError("Build manifest missing")
        if archive.getinfo("build-manifest.json").file_size > 1024 * 1024:
            raise DeploymentError("Manifest capacity exceeded")
        manifest = json.loads(archive.read("build-manifest.json"))
        if (manifest.get("BuildHash", "").upper() != build_hash or manifest.get("RuntimeCommit") != RUNTIME_COMMIT
                or manifest.get("CompilerVersion") != "STruC++ version 0.7.0"):
            raise DeploymentError("Unapproved Runtime/compiler/build identity")
        inventory = manifest.get("Files", {})
        if set(inventory) | {"build-manifest.json"} != names:
            raise DeploymentError("Archive inventory differs from manifest")
        for name, checksum in inventory.items():
            if hashlib.sha256(archive.read(name)).hexdigest().lower() != checksum.lower():
                raise DeploymentError("Archive content checksum mismatch: " + name)
        required = {"generated.cpp", "generated.hpp", "generated_debug.cpp", "defines.h", "debug-map.json", "configuration.st", "strucpp_runtime/include/debug_table.hpp"}
        if not required.issubset(names):
            raise DeploymentError("Required Runtime artifacts missing")
        try:
            axis_gate.validate_bundle(archive, manifest, names)
        except (ValueError, TypeError, OverflowError, UnicodeError) as error:
            raise DeploymentError("Invalid axis dependency bundle: " + str(error)) from error
        try:
            communication_gate.validate_bundle(archive, manifest, names)
        except (ValueError, TypeError, OverflowError, UnicodeError, AttributeError) as error:
            raise DeploymentError("Invalid communication dependency bundle: " + str(error)) from error
        motion_files = {"paths/motion-manifest.json", "paths/virtual_worker.py", "runtime_lifecycle.inc", "continuous_path.st", "motion_bridge.cpp", "motion_bridge.hpp", "generated_scan_shim.cpp"}
        if manifest.get("MotionInterfaceVersion") is not None:
            if manifest["MotionInterfaceVersion"] != "workbench-continuous-v1" or not motion_files.issubset(names):
                raise DeploymentError("Unapproved or incomplete motion dependency bundle")
        elif names.intersection({"paths/virtual_worker.py", "runtime_lifecycle.inc"}):
            raise DeploymentError("Motion executable dependencies require explicit interface identity")
        cycle_interface = manifest.get("MotionCycleInterfaceVersion")
        if cycle_interface is not None:
            if cycle_interface != motion_cycle.INTERFACE or "motion-cycle.json" not in names:
                raise DeploymentError("Unapproved or incomplete motion-cycle configuration")
            if archive.getinfo("motion-cycle.json").file_size > 1024 * 1024:
                raise DeploymentError("motion-cycle configuration capacity exceeded")
            try:
                motion_cycle.validate_configuration(json.loads(archive.read("motion-cycle.json")))
            except (ValueError, TypeError, OverflowError) as error:
                raise DeploymentError("Invalid motion-cycle configuration: " + str(error)) from error
        elif "motion-cycle.json" in names:
            raise DeploymentError("motion-cycle configuration requires explicit interface identity")
        if hashlib.sha256(archive.read("debug-map.json")).hexdigest().lower() != manifest.get("SymbolTableHash", "").lower():
            raise DeploymentError("Symbol table identity mismatch")
        return manifest


def atomic_json(path, data):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    with open(temporary, "w", encoding="utf8") as stream:
        json.dump(data, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)
    sync_directory(path.parent)


def sync_directory(path):
    if os.name == "posix":
        descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def read_json(path):
    return json.loads(pathlib.Path(path).read_text(encoding="utf8"))


def motion_candidate_source(relative, content, manifest):
    """Candidate-only entry hook; verify the pinned original before calling this."""
    motion = manifest.get("MotionInterfaceVersion")
    axis = manifest.get("AxisInterfaceVersion")
    communication = manifest.get("CommunicationInterfaceVersion")
    if motion is None and axis is None and communication is None:
        return content
    if motion is not None and motion != "workbench-continuous-v1":
        raise DeploymentError("Unsupported motion interface")
    if axis is not None and axis != axis_gate.INTERFACE:
        raise DeploymentError("Unsupported axis interface")
    if communication is not None and communication != communication_gate.INTERFACE:
        raise DeploymentError("Unsupported communication interface")
    if relative == "core/strucpp_runtime/runtime_v4_entry.cpp":
        entry = b'extern "C" strucpp::ConfigurationInstance* strucpp_get_config(void) {\n    return &g_config;\n}'
        if content.count(entry) != 1:
            raise DeploymentError("Pinned Runtime motion bootstrap location changed")
        includes, calls = b'', b''
        if motion is not None:
            includes += b'#include "runtime_lifecycle.inc"\n'
            calls += b'    if (!workbench_motion_bootstrap()) return nullptr;\n'
        if axis is not None:
            includes += b'#include "axis_lifecycle.inc"\n'
            calls += b'    if (!workbench_axis_bootstrap()) return nullptr;\n'
        if communication is not None:
            includes += b'#include "communication_lifecycle.inc"\n'
            calls += b'    if (!workbench_comm_bootstrap()) return nullptr;\n'
        hook = includes + entry.replace(b'    return &g_config;', calls + b'    return &g_config;')
        return content.replace(entry, hook)
    return content


def verify_axis_ready(manifest, generated, config, environment):
    """Verify only local worker metadata; never connect or issue motion commands."""
    try:
        axis_gate.verify_ready(manifest, generated, config, environment)
    except (OSError, ValueError, TypeError, OverflowError, UnicodeError) as error:
        raise DeploymentError("Axis worker unavailable or invalid: " + str(error)) from error


def verify_communication_ready(manifest, generated, config, environment):
    """Local metadata only; never connects peers or opens serial devices."""
    try:
        communication_gate.verify_ready(manifest, generated, config, environment)
    except (OSError, ValueError, TypeError, OverflowError, UnicodeError) as error:
        raise DeploymentError("Communication worker unavailable or invalid: " + str(error)) from error


def verify_motion_ready(manifest, generated, config, environment):
    """No actual device writes. Motion bundles need an explicit matching virtual worker."""
    interface = manifest.get("MotionInterfaceVersion")
    if interface is None:
        return
    if interface != "workbench-continuous-v1" or config.get("motion_backend") != "VirtualContinuous":
        raise DeploymentError("Continuous motion backend is not configured; physical Continuous is not supported")
    endpoint = config.get("motion_socket_path", "")
    if not endpoint.startswith("/") or len(endpoint.encode()) >= 104:
        raise DeploymentError("A private absolute motion socket path is required")
    values = dict(value.split("=", 1) for value in shlex.split(environment) if "=" in value)
    if values.get("STWORKBENCH_MOTION_BACKEND") != "VirtualContinuous" or values.get("STWORKBENCH_MOTION_SOCKET") != endpoint:
        raise DeploymentError("Runtime motion environment does not match the configured private worker")
    socket_path = pathlib.Path(endpoint)
    sidecar = pathlib.Path(endpoint + ".capabilities.json")
    try:
        sock_stat, meta_stat = socket_path.lstat(), sidecar.lstat()
        if not stat.S_ISSOCK(sock_stat.st_mode) or not stat.S_ISREG(meta_stat.st_mode) or sock_stat.st_uid != os.getuid() or meta_stat.st_uid != os.getuid() or stat.S_IMODE(meta_stat.st_mode) != 0o600 or meta_stat.st_size > 65536:
            raise DeploymentError("Motion worker socket/capability ownership is invalid")
        capability = read_json(sidecar)
        pid = capability.get("PID")
        if not isinstance(pid, int) or pid <= 0:
            raise DeploymentError("Invalid motion worker PID")
        os.kill(pid, 0)
        process = pathlib.Path("/proc") / str(pid)
        if process.stat().st_uid != os.getuid():
            raise DeploymentError("Motion worker process owner differs")
        bundle = pathlib.Path(generated) / "paths" / "motion-manifest.json"
        if capability.get("Backend") != "VirtualContinuous" or capability.get("PhysicalContinuous") is not False or capability.get("ManifestSha256") != digest(bundle):
            raise DeploymentError("Motion worker capability or path manifest identity differs")
        data = read_json(bundle)
        expected = [{key: item["Asset"][key] for key in ("Id", "NumericId", "Version", "ContentHash", "Axes")} for item in data["Paths"]]
        if capability.get("Paths") != expected or data.get("Backend") != "VirtualContinuous":
            raise DeploymentError("Motion worker path resources differ from this candidate")
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise DeploymentError("Motion worker is unavailable or its capability data is invalid") from error


class RuntimeConnection:
    def __init__(self, path, timeout=3):
        self.path = path
        self.timeout = timeout

    def __enter__(self):
        # Tuple endpoints exist only for portable fake-daemon tests. Production config uses an AF_UNIX path.
        self.socket = socket.socket(socket.AF_INET if isinstance(self.path, tuple) else socket.AF_UNIX, socket.SOCK_STREAM)
        self.socket.settimeout(self.timeout)
        self.socket.connect(self.path)
        return self

    def __exit__(self, *args):
        self.socket.close()

    def command(self, command):
        if command not in {"STATUS", "START", "STOP"}:
            raise DeploymentError("Unsupported IPC command")
        self.socket.sendall((command + "\n").encode("ascii"))
        response = bytearray()
        while not response.endswith(b"\n"):
            chunk = self.socket.recv(1)
            if not chunk or len(response) >= 128:
                raise DeploymentError("Incomplete or oversized Runtime response")
            response.extend(chunk)
        return response.decode("ascii").strip()

    def status(self):
        response = self.command("STATUS")
        if not response.startswith("STATUS:"):
            raise DeploymentError("Invalid Runtime status response")
        return response[7:]

    def transition(self, action, expected):
        state = self.status()
        if state == expected:
            return state
        if state not in ({"STOPPED", "EMPTY"} if action == "START" else {"RUNNING"}):
            raise DeploymentError("Runtime state cannot accept explicit " + action + ": " + state)
        if self.command(action) != action + ":OK":
            raise DeploymentError("Runtime refused explicit " + action)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            state = self.status()
            if state == expected:
                return state
            if state != "TRANSITIONING":
                raise DeploymentError("Runtime transition failed: " + state)
            time.sleep(.05)
        raise DeploymentError("Runtime transition outcome unknown; query status, do not resend")


class RestRuntimeConnection:
    """Authenticated local REST forwards through the webserver's sole native IPC owner."""
    def __init__(self, config):
        self.config = config
        self.context = ssl.create_default_context(cafile=config["ca_file"])

    def __enter__(self):
        credentials = pathlib.Path(self.config["credentials_file"])
        if os.name == "posix" and (stat.S_IMODE(credentials.stat().st_mode) != 0o600 or credentials.stat().st_uid != os.getuid()):
            raise DeploymentError("Private coordinator credentials must be owner-only mode0600")
        login = self.call("/api/login", read_json(credentials))
        self.token = login["access_token"]
        return self

    def __exit__(self, *args):
        self.token = None

    def call(self, path, body=None):
        # The URL is fixed to this private local Runtime, with normal CA/hostname validation.
        headers = {"Content-Type": "application/json"}
        if getattr(self, "token", None):
            headers["Authorization"] = "Bearer " + self.token
        request = urllib.request.Request("https://localhost:8443" + path, data=json.dumps(body).encode("utf8") if body is not None else None, headers=headers)
        with urllib.request.urlopen(request, timeout=3, context=self.context) as response:
            raw = response.read(1024 * 1024 + 1)
            if len(raw) > 1024 * 1024:
                raise DeploymentError("Runtime response capacity exceeded")
            return json.loads(raw)

    def status(self):
        response = self.call("/api/status").get("status", "UNKNOWN")
        return response[7:] if response.startswith("STATUS:") else "UNKNOWN"

    def transition(self, action, expected):
        state = self.status()
        if state == expected:
            return state
        if state not in ({"STOPPED", "EMPTY"} if action == "START" else {"RUNNING"}):
            raise DeploymentError("Runtime cannot accept explicit " + action + ": " + state)
        self.call("/api/start-plc" if action == "START" else "/api/stop-plc")
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            state = self.status()
            if state == expected:
                return state
            if state != "TRANSITIONING":
                raise DeploymentError("Runtime transition did not reach expected state: " + state)
            time.sleep(.1)
        raise DeploymentError("Runtime transition result unknown; query status")


class Coordinator:
    def __init__(self, config):
        self.config = config
        if config.get("runtime_commit") != RUNTIME_COMMIT or config.get("exclusive_control") is not True:
            raise DeploymentError("Pinned private Runtime with exclusive control is required")
        self.prefix = pathlib.Path(config["prefix"]).resolve()
        self.runtime = pathlib.Path(config["runtime_source"]).resolve()
        self.prefix.mkdir(parents=True, exist_ok=True)
        self.active_path = self.prefix / "active.json"
        self.transaction_path = self.prefix / "transaction.json"

    def connection(self):
        return RestRuntimeConnection(self.config) if self.config.get("control_transport") == "rest" else RuntimeConnection(self.config["socket_path"])

    def service(self, action):
        if self.config.get("control_transport") != "rest":
            return
        if self.config.get("service_name") != "stworkbench-openplc.service" or self.config.get("safe_mode_verified") is not True:
            raise DeploymentError("Exact private service and safe-mode launcher verification are required")
        unit = "stworkbench-openplc.service"
        properties = subprocess.run(["systemctl", "--user", "show", unit, "--property=KillMode"], capture_output=True, text=True, timeout=10, check=True).stdout.strip()
        if properties != "KillMode=control-group":
            raise DeploymentError("Service teardown must terminate the complete daemon process group")
        subprocess.run(["systemctl", "--user", action, unit], capture_output=True, timeout=30, check=True)
        if action == "stop":
            self.inactive_service()

    def inactive_service(self):
        result = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=ActiveState", "--property=MainPID"], capture_output=True, text=True, timeout=10, check=True).stdout
        if "ActiveState=inactive" not in result or "MainPID=0" not in result:
            raise DeploymentError("Private Runtime service is not confirmed inactive")

    def resume_service(self, allow_empty=False):
        if self.config.get("control_transport") != "rest":
            return
        self.service("start")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                with self.connection() as connection:
                    self.stopped(connection, allow_empty)
                return
            except (OSError, DeploymentError):
                time.sleep(.25)
        raise DeploymentError("Restarted service state unknown; PLC was never requested to START")

    @contextlib.contextmanager
    def lock(self):
        with open(self.prefix / "owner.lock", "a+b") as stream:
            if os.name == "posix":
                import fcntl
                try:
                    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError as error:
                    raise DeploymentError("Another deployment owns the instance") from error
            else:
                import msvcrt
                stream.seek(0)
                stream.write(b"0")
                stream.flush()
                stream.seek(0)
                try:
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as error:
                    raise DeploymentError("Another deployment owns the instance") from error
            yield

    def version(self, build_hash):
        return self.prefix / ".versions" / identity(build_hash)

    def libraries(self):
        directory = self.runtime / "build"
        directory.mkdir(parents=True, exist_ok=True)
        return [path for path in directory.iterdir() if path.name.startswith("libplc_") and ".so" in path.name]

    def active(self):
        if not self.active_path.exists():
            raise DeploymentError("No adopted baseline; explicitly adopt stopped or empty baseline first")
        return read_json(self.active_path)

    def verify_active(self, active, expected):
        if active["build_hash"] != expected:
            raise DeploymentError("Active identity changed; refresh before retry")
        libraries = self.libraries()
        if active["build_hash"] is None:
            if libraries:
                raise DeploymentError("Empty baseline now contains unexpected program libraries")
        elif len(libraries) != 1 or str(libraries[0]) != active["library_path"] or digest(libraries[0]) != active["library_sha256"]:
            raise DeploymentError("Active library bytes/path differ from adopted identity")

    def stopped(self, connection, empty_allowed=False):
        state = connection.status()
        if state != "STOPPED" and not (empty_allowed and state == "EMPTY"):
            raise DeploymentError("Activation requires confirmed " + ("EMPTY" if empty_allowed else "STOPPED") + "; actual " + state)

    def stage(self, request):
        build_hash = identity(request["build_hash"])
        if request.get("package_base64"):
            encoded = request["package_base64"]
            if len(encoded) > MAX_ARCHIVE * 4 // 3 + 4:
                raise DeploymentError("Package transport capacity exceeded")
            incoming = self.prefix / "incoming"
            incoming.mkdir(exist_ok=True)
            package = incoming / (request["request_id"] + ".zip")
            package.write_bytes(base64.b64decode(encoded, validate=True))
        else:
            package = pathlib.Path(request["package_path"])
        manifest = validate_archive(package, request["package_sha256"], build_hash)
        version = self.version(build_hash)
        if version.exists():
            # Transport bytes include ZIP timestamps, order and compression. Reuse only
            # after validating the complete incoming manifest and existing staged bytes.
            staged = read_json(version / "stage-result.json")
            generated = version / "core" / "generated"
            if staged.get("manifest") != manifest or read_json(generated / "build-manifest.json") != manifest:
                raise DeploymentError("Build identity already staged with different content or manifest")
            for relative, checksum in manifest["Files"].items():
                source = generated / relative
                if not source.is_file() or source.is_symlink() or digest(source).lower() != checksum.lower():
                    raise DeploymentError("Existing staged content changed: " + relative)
            return {"state": "staged", "build_hash": build_hash}
        temporary = version.with_name(version.name + "." + uuid.uuid4().hex + ".tmp")
        generated = temporary / "core" / "generated"
        generated.mkdir(parents=True)
        with zipfile.ZipFile(package) as archive:
            for entry in archive.infolist():
                target = generated / entry.filename
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(entry.filename))
        atomic_json(temporary / "stage-result.json", {"state": "staged", "build_hash": build_hash,
            "package_sha256": request["package_sha256"], "manifest": manifest, "request_id": request["request_id"]})
        version.parent.mkdir(parents=True, exist_ok=True)
        os.replace(temporary, version)
        return {"state": "staged", "build_hash": build_hash}

    def compile(self, request):
        version = self.version(request["build_hash"])
        stage = read_json(version / "stage-result.json")
        profile = read_json(pathlib.Path(__file__).with_name("runtime-profile.json"))
        for relative, checksum in profile["source_hashes"].items():
            source = self.runtime / relative
            compiled_source = source.read_bytes().replace(b"\r\n", b"\n")
            canonical_source = canonical_pinned_source(relative, compiled_source)
            if hashlib.sha256(canonical_source).hexdigest().upper() != checksum:
                raise DeploymentError("Pinned Runtime compile source changed: " + relative)
            target = version / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(motion_candidate_source(relative, compiled_source, stage["manifest"]))
        # Candidate source files must still match the original staged inventory before compiling.
        for relative, checksum in stage["manifest"]["Files"].items():
            if digest(version / "core" / "generated" / relative).upper() != checksum:
                raise DeploymentError("Staged source content changed: " + relative)
        log_path = version / "compile.log"
        exit_code = run_bounded(["make", "-j1", "-f", "scripts/Makefile.strucpp"], version, log_path, 300)
        library = version / "build" / "new_libplc.so"
        if exit_code != 0 or not library.is_file() or library.stat().st_size == 0:
            result = {"state": "compile-failed", "build_hash": identity(request["build_hash"]), "exit_code": exit_code, "log_path": str(log_path)}
        else:
            export_path = version / "exports.txt"
            if run_bounded(["nm", "-D", "--defined-only", str(library)], version, export_path, 15) != 0:
                raise DeploymentError("Candidate symbol inspection failed")
            exports = {line.split()[-1] for line in export_path.read_text().splitlines() if line.split()}
            if not REQUIRED_EXPORTS.issubset(exports):
                raise DeploymentError("Candidate missing pinned Runtime exports: " + ",".join(sorted(REQUIRED_EXPORTS - exports)))
            result = {"state": "compiled", "build_hash": identity(request["build_hash"]), "library_sha256": digest(library),
                "exports_verified": True, "runtime_commit": RUNTIME_COMMIT, "log_path": str(log_path), "request_id": request["request_id"]}
        result["log"] = log_path.read_text(encoding="utf8", errors="replace")[-65536:]
        if stage["manifest"].get("AxisInterfaceVersion") is None:
            try:
                verify_axis_ready(stage["manifest"], version / "core" / "generated", self.config, "")
            except DeploymentError as error:
                result["axis_ready"] = False
                result["axis_message"] = str(error)
        if stage["manifest"].get("MotionCycleInterfaceVersion") is not None:
            result["motion_cycle_ready"] = False
            result["motion_cycle_message"] = motion_cycle.ADAPTER_MESSAGE
        if stage["manifest"].get("AxisInterfaceVersion") is not None:
            result["axis_ready"] = False
            result["axis_message"] = "Basic axis backend not explicitly configured"
            if self.config.get("axis_backend") in {"EcMotion", "EcMotionSimulator"}:
                try:
                    environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
                    verify_axis_ready(stage["manifest"], version / "core" / "generated", self.config, environment)
                    result["axis_ready"] = result["state"] == "compiled"
                    result["axis_message"] = "Verified private " + self.config["axis_backend"] + " basic axis backend; motion-cycle configuration is not applied"
                except (DeploymentError, OSError, subprocess.SubprocessError) as error:
                    result["axis_message"] = str(error)
        if stage["manifest"].get("MotionInterfaceVersion") is not None:
            result["motion_ready"] = False
            result["motion_message"] = "Continuous backend not configured; this candidate cannot be activated for physical motion"
            if self.config.get("motion_backend") == "VirtualContinuous":
                try:
                    environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
                    verify_motion_ready(stage["manifest"], version / "core" / "generated", self.config, environment)
                    result["motion_ready"] = result["state"] == "compiled"
                    result["motion_message"] = "Verified isolated VirtualContinuous backend"
                except (DeploymentError, OSError, subprocess.SubprocessError) as error:
                    result["motion_message"] = str(error)
        if stage["manifest"].get("CommunicationInterfaceVersion") is not None:
            result["communication_ready"] = False
            result["communication_message"] = "Communication NativeClient backend not explicitly configured"
            if self.config.get("communication_backend") == "NativeClient":
                try:
                    environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
                    verify_communication_ready(stage["manifest"], version / "core" / "generated", self.config, environment)
                    result["communication_ready"] = result["state"] == "compiled"
                    result["communication_message"] = "Verified private NativeClient worker metadata; no device connection performed"
                except (DeploymentError, OSError, subprocess.SubprocessError) as error:
                    result["communication_message"] = str(error)
        atomic_json(version / "compile-result.json", result)
        return result

    def adopt(self, request, empty):
        if self.active_path.exists():
            raise DeploymentError("Baseline already adopted")
        libraries = self.libraries()
        with self.connection() as connection:
            state = connection.status()
            if empty:
                permitted_empty = state == "EMPTY" or (state == "STOPPED" and self.config.get("safe_mode_verified") is True)
                if not permitted_empty or libraries:
                    raise DeploymentError("Empty baseline requires EMPTY or verified safe-mode STOPPED and no matching program libraries")
                active = {"build_hash": None, "library_path": str(self.runtime / "build" / "libplc_stworkbench.so"), "library_sha256": None}
            else:
                if state != "STOPPED" or len(libraries) != 1 or libraries[0].is_symlink():
                    raise DeploymentError("Baseline adoption requires STOPPED and exactly one regular program library")
                checksum = digest(libraries[0])
                active = {"build_hash": "baseline-" + checksum, "library_path": str(libraries[0]), "library_sha256": checksum}
            active.update({"request_id": request["request_id"], "runtime_commit": RUNTIME_COMMIT})
            atomic_json(self.active_path, active)
        return {"state": "baseline-empty" if empty else "baseline-adopted", "active": active}

    def activate(self, request):
        active = self.active()
        self.verify_active(active, request.get("expected_active_build_hash"))
        version = self.version(request["build_hash"])
        compiled = read_json(version / "compile-result.json")
        library = version / "build" / "new_libplc.so"
        if compiled.get("state") != "compiled" or compiled.get("exports_verified") is not True or digest(library) != compiled["library_sha256"]:
            raise DeploymentError("Candidate has no verified successful compilation")
        manifest = read_json(version / "stage-result.json")["manifest"]
        if manifest.get("MotionCycleInterfaceVersion") is not None:
            raise DeploymentError(motion_cycle.ADAPTER_MESSAGE)
        if manifest.get("AxisInterfaceVersion") is None:
            verify_axis_ready(manifest, version / "core" / "generated", self.config, "")
        if manifest.get("CommunicationInterfaceVersion") is not None:
            if compiled.get("communication_ready") is not True:
                raise DeploymentError("Candidate communication readiness was not verified")
            environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
            verify_communication_ready(manifest, version / "core" / "generated", self.config, environment)
        if manifest.get("AxisInterfaceVersion") is not None:
            if compiled.get("axis_ready") is not True:
                raise DeploymentError("Candidate basic axis readiness was not verified")
            environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
            verify_axis_ready(manifest, version / "core" / "generated", self.config, environment)
        if manifest.get("MotionInterfaceVersion") is not None:
            environment = subprocess.run(["systemctl", "--user", "show", "stworkbench-openplc.service", "--property=Environment", "--value"], capture_output=True, text=True, timeout=10, check=True).stdout
            verify_motion_ready(manifest, version / "core" / "generated", self.config, environment)
        target = pathlib.Path(active["library_path"])
        with self.connection() as connection:
            self.stopped(connection, active["build_hash"] is None)
            history = self.prefix / ".history"
            history.mkdir(exist_ok=True)
            backup = history / (request["request_id"] + ".so")
            if target.exists():
                durable_copy(target, backup)
            next_active = {"build_hash": identity(request["build_hash"]), "library_path": str(target), "library_sha256": digest(library),
                "program_md5": manifest["ProgramMd5"], "symbol_table_hash": manifest["SymbolTableHash"], "hardware_hash": manifest["HardwareHash"],
                "runtime_commit": RUNTIME_COMMIT, "request_id": request["request_id"]}
            transaction = {"state": "prepared", "request_id": request["request_id"], "old_active": active, "new_active": next_active,
                "backup_path": str(backup), "candidate_path": str(library)}
            atomic_json(self.transaction_path, transaction)
            temporary = target.with_name(".candidate-" + request["request_id"] + ".tmp")
            try:
                self.service("stop")
                durable_copy(library, temporary)
                with open(temporary, "r+b") as stream:
                    os.fsync(stream.fileno())
                self.verify_active(active, request.get("expected_active_build_hash"))
                if self.config.get("control_transport") == "rest":
                    self.inactive_service()
                else:
                    self.stopped(connection, active["build_hash"] is None)
                os.replace(temporary, target)
                sync_directory(target.parent)
                transaction["state"] = "swapped"
                atomic_json(self.transaction_path, transaction)
                atomic_json(self.active_path, next_active)
                transaction["state"] = "committed"
                atomic_json(self.transaction_path, transaction)
                self.resume_service()
            except Exception:
                self.service("stop")
                if backup.exists():
                    restore = target.with_name(".restore-" + request["request_id"] + ".tmp")
                    if digest(backup) != active["library_sha256"]:
                        raise DeploymentError("Rollback backup checksum differs; explicit recovery required")
                    durable_copy(backup, restore)
                    os.replace(restore, target)
                    sync_directory(target.parent)
                elif active["build_hash"] is None and target.exists():
                    target.unlink()
                    sync_directory(target.parent)
                atomic_json(self.active_path, active)
                transaction["state"] = "rolled-back"
                atomic_json(self.transaction_path, transaction)
                self.resume_service(active["build_hash"] is None)
                raise
        return {"state": "activated-not-running", "active": next_active}

    def recover(self, request):
        transaction = read_json(self.transaction_path)
        if transaction["state"] in {"committed", "rolled-back"}:
            active = self.active()
            expected = transaction["new_active" if transaction["state"] == "committed" else "old_active"]
            if active != expected:
                raise DeploymentError("Terminal journal differs from active identity")
            self.verify_active(active, expected["build_hash"])
            self.resume_service(active["build_hash"] is None)
            return {"state": transaction["state"], "active": active}
        old = transaction["old_active"]
        target = pathlib.Path(old["library_path"])
        with contextlib.ExitStack() as context:
            if self.config.get("control_transport") == "rest":
                self.service("stop")
            else:
                connection = context.enter_context(self.connection())
                self.stopped(connection, old["build_hash"] is None)
            backup = pathlib.Path(transaction["backup_path"])
            if old["build_hash"] is None:
                if target.exists():
                    target.unlink()
                    sync_directory(target.parent)
            elif not backup.exists() or digest(backup) != old["library_sha256"]:
                raise DeploymentError("Cannot recover: original baseline backup checksum missing/mismatched")
            else:
                temporary = target.with_name(".recovery-" + request["request_id"] + ".tmp")
                durable_copy(backup, temporary)
                os.replace(temporary, target)
                sync_directory(target.parent)
            atomic_json(self.active_path, old)
            transaction["state"] = "rolled-back"
            atomic_json(self.transaction_path, transaction)
            self.resume_service(old["build_hash"] is None)
        return {"state": "rolled-back", "active": old}

    def execute(self, request):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", request.get("request_id", "")):
            raise DeploymentError("Request ID is required and must be a safe identifier")
        action = request.get("action")
        if action == "realtime-status":
            return {"success": True, "state": "diagnostic", "request_id": request["request_id"], "realtime": motion_cycle.realtime_status()}
        with self.lock():
            if self.transaction_path.exists() and read_json(self.transaction_path)["state"] not in {"committed", "rolled-back"} and action not in {"status", "recover"}:
                raise DeploymentError("An unfinished activation transaction requires explicit recovery")
            if action == "stage":
                result = self.stage(request)
            elif action == "compile":
                try:
                    result = self.compile(request)
                except Exception as error:
                    result = {"state": "compile-failed", "build_hash": identity(request["build_hash"]), "message": str(error), "request_id": request["request_id"]}
                    version = self.version(request["build_hash"])
                    if version.exists():
                        atomic_json(version / "compile-result.json", result)
            elif action in {"adopt-baseline", "adopt-empty-baseline"}:
                result = self.adopt(request, action == "adopt-empty-baseline")
            elif action == "activate":
                result = self.activate(request)
            elif action == "recover":
                result = self.recover(request)
            elif action == "status":
                result = {"state": "unavailable", "runtime_confirmed": False, "active": self.active() if self.active_path.exists() else None,
                    "transaction": read_json(self.transaction_path) if self.transaction_path.exists() else None}
                if request.get("build_hash"):
                    version = self.version(request["build_hash"])
                    result["candidate"] = read_json(version / "compile-result.json") if (version / "compile-result.json").exists() else read_json(version / "stage-result.json")
                try:
                    with self.connection() as connection:
                        result["state"] = connection.status().lower()
                        result["runtime_confirmed"] = True
                except (OSError, DeploymentError, TimeoutError):
                    result["message"] = "Runtime unavailable; disk identity and journal returned, runtime state is unconfirmed"
            elif action in {"start", "stop"}:
                with self.connection() as connection:
                    if action == "status":
                        result = {"state": connection.status().lower(), "active": self.active() if self.active_path.exists() else None,
                            "transaction": read_json(self.transaction_path) if self.transaction_path.exists() else None}
                        if request.get("build_hash"):
                            version = self.version(request["build_hash"])
                            result["candidate"] = read_json(version / "compile-result.json") if (version / "compile-result.json").exists() else read_json(version / "stage-result.json")
                    else:
                        active = self.active()
                        self.verify_active(active, request.get("expected_active_build_hash"))
                        if action == "start" and active["build_hash"] is None:
                            raise DeploymentError("Cannot start an empty baseline")
                        result = {"state": connection.transition(action.upper(), "RUNNING" if action == "start" else "STOPPED").lower(), "active": active}
            else:
                raise DeploymentError("Unsupported explicit deployment action")
            result.update({"success": result["state"] != "compile-failed", "request_id": request["request_id"]})
            return result


def run_bounded(arguments, directory, log_path, timeout):
    with open(log_path, "wb") as output:
        process = subprocess.Popen(arguments, cwd=directory, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
        def drain():
            length = 0
            while True:
                chunk = process.stdout.read(4096)
                if not chunk:
                    break
                allowed = max(0, MAX_LOG - length)
                output.write(chunk[:allowed])
                length += min(len(chunk), allowed)
        thread = threading.Thread(target=drain, daemon=True)
        thread.start()
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGKILL)
            else:
                process.kill()
            process.wait(timeout=5)
            code = -1
        thread.join(5)
        if thread.is_alive():
            raise DeploymentError("Compiler descendant output did not close")
        return code


if __name__ == "__main__":
    os.umask(0o077)
    request = {}
    try:
        raw = sys.stdin.buffer.read(MAX_ARCHIVE * 2 + 1)
        if len(raw) > MAX_ARCHIVE * 2:
            raise DeploymentError("Request capacity exceeded")
        request = json.loads(raw)
        config = read_json(pathlib.Path(__file__).with_name("config.json"))
        if config.get("control_transport") != "rest" or config.get("prefix") != "/home/a/.local/share/st-workbench/runtime-v4.3.0/deployment" or config.get("runtime_source") != "/home/a/.local/share/st-workbench/runtime-v4.3.0/source":
            raise DeploymentError("Production configuration must identify the exact private Runtime/prefix and REST transport")
        response = Coordinator(config).execute(request)
    except Exception as error:
        response = {"success": False, "state": "failed-or-unknown", "request_id": request.get("request_id"), "message": str(error)}
    print(json.dumps(response))
    sys.exit(0 if response.get("success") else 1)
