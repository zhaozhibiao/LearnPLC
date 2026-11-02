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
import ethercat_hardware
import configuration_download
import scoped_transfer

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


def validate_archive(path, package_sha256, build_hash, hardware_only=False):
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
            if not (name in {"LICENSE", "COPYING", "COPYING.RUNTIME", "RUNTIME-COMPAT-LICENSE", "control/LICENSE", "paths/LICENSE", "paths/virtual_worker.py", "runtime_lifecycle.inc", "axis_lifecycle.inc", "axes/axis_worker.py", "axes/axis_worker_v2.py", "axes/LICENSE"} or name in communication_gate.FILES or pathlib.PurePosixPath(name).suffix in {".cpp", ".hpp", ".h", ".st", ".json"}):
                raise DeploymentError("Unexpected archive file: " + name)
            names.add(name)
        if "build-manifest.json" not in names:
            raise DeploymentError("Build manifest missing")
        if archive.getinfo("build-manifest.json").file_size > 1024 * 1024:
            raise DeploymentError("Manifest capacity exceeded")
        manifest = json.loads(archive.read("build-manifest.json"))
        if (manifest.get("BuildHash", "").upper() != build_hash or (not hardware_only and
                (manifest.get("ArtifactKind") == "hardware-only" or manifest.get("RuntimeCommit") != RUNTIME_COMMIT
                or manifest.get("CompilerVersion") != "STruC++ version 0.7.0"))):
            raise DeploymentError("Unapproved Runtime/compiler/build identity")
        task_interface = manifest.get("TaskSchedulingInterfaceVersion")
        if task_interface not in (None, "workbench-task-scheduling-v1") or bool(task_interface) != ("task-scheduling.json" in names):
            raise DeploymentError("Task scheduling manifest/interface identity differs")
        inventory = manifest.get("Files", {})
        if set(inventory) | {"build-manifest.json"} != names:
            raise DeploymentError("Archive inventory differs from manifest")
        for name, checksum in inventory.items():
            if hashlib.sha256(archive.read(name)).hexdigest().lower() != checksum.lower():
                raise DeploymentError("Archive content checksum mismatch: " + name)
        runtime_interface = manifest.get("EtherCatRuntimeInterfaceVersion")
        if runtime_interface is None:
            if set(ethercat_hardware.FILES).intersection(names):
                raise DeploymentError("EtherCAT runtime files require an explicit interface identity")
        else:
            if runtime_interface != "workbench-ethercat-v1" or not set(ethercat_hardware.FILES).issubset(names):
                raise DeploymentError("Unsupported or incomplete EtherCAT runtime bundle")
            try:
                contents = {name: archive.read(name) for name in ethercat_hardware.FILES}
                hashes = {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()}
                for data in contents.values():
                    if len(data) > ethercat_hardware.MAX_JSON:
                        raise ValueError("Configuration capacity exceeded")
                    ethercat_hardware.parse_json(data)
                fingerprint = ethercat_hardware.validate_topology(ethercat_hardware.parse_json(contents["ethercat-runtime.json"]), hashes)
                if manifest.get("EtherCatTopologyFingerprint") != fingerprint:
                    raise ValueError("Build manifest topology fingerprint differs")
            except (ValueError, TypeError, KeyError, UnicodeError) as error:
                raise DeploymentError("Invalid EtherCAT runtime bundle: " + str(error)) from error
        if hardware_only:
            required = set(ethercat_hardware.FILES) | {'axis-manifest.json', 'hardware-configuration.json', 'build-manifest.json'}
            if manifest.get('ArtifactKind') != 'hardware-only' or names != required or runtime_interface != 'workbench-ethercat-v1':
                raise DeploymentError('Unsupported standalone hardware bundle')
            try:
                scoped_transfer.validate_axes(axis_gate.strict_json(archive.read('axis-manifest.json')))
                configuration = ethercat_hardware.parse_json(archive.read('hardware-configuration.json'))
                if configuration.get('SchemaVersion') != 1 or configuration.get('State') != 'offline-engineering':
                    raise ValueError('Unsupported saved hardware configuration')
                if hashlib.sha256(archive.read('hardware-configuration.json')).hexdigest() != identity(manifest.get('HardwareHash')).lower():
                    raise ValueError('Hardware engineering identity differs')
            except (ValueError, TypeError, KeyError, UnicodeError) as error:
                raise DeploymentError('Invalid standalone hardware bundle: ' + str(error)) from error
            return manifest
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
    if axis is not None and axis not in axis_gate.INTERFACES:
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


def verify_candidate_exports(manifest, exports):
    required = REQUIRED_EXPORTS | ({"workbench_task_scheduling_v1"} if manifest.get("TaskSchedulingInterfaceVersion") else set())
    if manifest.get('AxisInterfaceVersion')==axis_gate.INTERFACE_V2:
        if manifest.get('AxisExportMarker')!='workbench_axis_v2':raise DeploymentError('V2 axis export marker is missing')
        required |= {'workbench_axis_v2'}
    if not required.issubset(exports):
        raise DeploymentError("Candidate missing pinned Runtime exports: " + ",".join(sorted(required - exports)))


def verify_task_scheduling(connection, manifest):
    version = manifest.get("TaskSchedulingInterfaceVersion")
    if version is None:
        return
    capability = getattr(connection, "task_scheduling_supported", None)
    if version != "workbench-task-scheduling-v1" or not callable(capability) or capability() is not True:
        raise DeploymentError("Runtime lacks verified task scheduling v1; upgrade runtime before activation")


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
        if command not in {"STATUS", "START", "STOP", "DEBUG:46 02"}:
            raise DeploymentError("Unsupported IPC command")
        self.socket.sendall((command + "\n").encode("ascii"))
        response = bytearray()
        while not response.endswith(b"\n"):
            chunk = self.socket.recv(1)
            if not chunk or len(response) >= 128:
                raise DeploymentError("Incomplete or oversized Runtime response")
            response.extend(chunk)
        return response.decode("ascii").strip()

    def task_scheduling_supported(self):
        response = self.command("DEBUG:46 02")
        try:
            return response.startswith("DEBUG:") and bytes.fromhex(response[6:]) == bytes([0x46, 0x7e, 1])
        except ValueError:
            return False

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

    def task_scheduling_supported(self):
        response = self.call('/api/workbench-task-scheduling')
        return (isinstance(response, dict) and response.get('interface') == 'workbench-task-scheduling-v1'
                and response.get('supported') is True)

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

    def resume_axis_v2_version(self):
        """Preflight the committed active v2 channel before starting either service."""
        if not self.active_path.exists():return None
        active=self.active()
        build=active.get('build_hash')
        if not isinstance(build,str) or re.fullmatch('[0-9A-Fa-f]{64}',build) is None:return None
        version=self.version(build)
        stage=version/'stage-result.json'
        if not stage.exists() or read_json(stage).get('manifest',{}).get('AxisInterfaceVersion')!=axis_gate.INTERFACE_V2:return None
        try:
            generated,manifest,compiled=configuration_download.candidate(version)
            self.verify_active(active,build)
            if (manifest.get('AxisInterfaceVersion')!=axis_gate.INTERFACE_V2
                    or compiled.get('library_sha256')!=active.get('library_sha256')):
                raise ValueError('Active v2 library differs from verified staged candidate')
            raw=ethercat_hardware.read_regular(generated/'axis-manifest.json')
            data=axis_gate.validate_manifest(axis_gate.strict_json(raw))
            if data['InterfaceVersion']!=axis_gate.INTERFACE_V2:raise ValueError('V2 manifest required')
            axis_gate.verify_native_profile_capabilities(data,configuration_download.native_state(self.config))
            configuration_download.validate_config(self.config,axis_gate.INTERFACE_V2)
            destination=configuration_download.manifest_destination(self.config,axis_gate.INTERFACE_V2)
            for path,name in [(destination,'axis-manifest.json'),(destination.with_name('axis_worker_v2.py'),'axes/axis_worker_v2.py')]:
                if path.is_symlink():raise ValueError('Symlink in installed v2 worker resources')
                info=path.stat()
                if (not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600
                        or configuration_download.source_digest(path)!=configuration_download.source_digest(generated/name)):
                    raise ValueError('Installed v2 worker/manifest differs from active candidate')
            return version
        except (OSError,ValueError,KeyError,subprocess.SubprocessError) as error:
            raise DeploymentError('Active v2 bridge cannot resume safely: '+str(error)) from error

    def resume_service(self, allow_empty=False):
        if self.config.get("control_transport") != "rest":
            return
        axis_v2=self.resume_axis_v2_version()
        self.service("start")
        if axis_v2 is not None:
            try:
                configuration_download.service('start',axis_gate.INTERFACE_V2)
                configuration_download.wait_ready(axis_v2,self.config)
            except (OSError,ValueError,subprocess.SubprocessError) as error:
                raise DeploymentError('Resumed v2 bridge readiness is unconfirmed; PLC was never requested to START: '+str(error)) from error
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

    def active_hardware_matches(self, active):
        hardware_active = pathlib.Path(self.config["ethercat_active_record"]) if self.config.get("ethercat_active_record") else self.prefix / "hardware-active.json"
        if not isinstance(active.get("build_hash"), str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", active["build_hash"]):
            return not hardware_active.exists()
        stage = self.version(active["build_hash"]) / "stage-result.json"
        manifest = read_json(stage).get("manifest", {}) if stage.exists() else {}
        if hardware_active.exists():
            expected = read_json(hardware_active).get("topologyFingerprint")
            if (manifest.get("EtherCatRuntimeInterfaceVersion") != "workbench-ethercat-v1"
                    or manifest.get("EtherCatTopologyFingerprint") != expected):
                return False
        # Updating a shared axis bridge can change AXIS_REF index/name bindings
        # without changing the physical bus. An old application must not regain
        # permission to run against that different logical mapping.
        if manifest.get("AxisInterfaceVersion") is not None:
            try:
                environment = configuration_download.runtime_environment()
                verify_axis_ready(manifest, stage.parent / "core" / "generated", self.config, environment)
            except (DeploymentError, OSError, ValueError, KeyError, subprocess.SubprocessError):
                return False
        return True

    def verify_start_hardware(self, active):
        if not self.active_hardware_matches(active):
            raise DeploymentError("Active PLC hardware configuration is stale; activate a program matching the applied hardware before Start")
        if not re.fullmatch(r"[0-9A-Fa-f]{64}", active["build_hash"]):
            return  # Explicitly adopted legacy identity, with no newer hardware applied.
        version = self.version(active["build_hash"])
        stage = version / "stage-result.json"
        manifest = read_json(stage).get("manifest", {}) if stage.exists() else {}
        if manifest.get("EtherCatRuntimeInterfaceVersion") is not None:
            try:
                ethercat_hardware.verify_applied(version / "core" / "generated", self.config)
            except (ValueError, OSError, KeyError) as error:
                raise DeploymentError("Active hardware cannot be verified: " + str(error)) from error
        if manifest.get("AxisInterfaceVersion") is not None:
            try:
                environment = configuration_download.runtime_environment()
                verify_axis_ready(manifest, version / "core" / "generated", self.config, environment)
            except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
                raise DeploymentError("Active axis mapping cannot be verified: " + str(error)) from error

    @staticmethod
    def native_core_pid():
        result = subprocess.run(["systemctl", "show", "st-workbench-is620n.service", "--property=LoadState", "--property=ActiveState", "--property=MainPID"],
                                check=False, capture_output=True, text=True, timeout=10)
        fields = {}
        for line in result.stdout.splitlines():
            key, separator, value = line.partition('=')
            if not separator or key not in {'LoadState', 'ActiveState', 'MainPID'} or key in fields:
                raise DeploymentError('Native service state is unconfirmed')
            fields[key] = value
        if set(fields) != {'LoadState', 'ActiveState', 'MainPID'} or not fields['MainPID'].isascii() or not fields['MainPID'].isdecimal():
            raise DeploymentError('Native service state is unconfirmed')
        pid = int(fields['MainPID'])
        if fields['LoadState'] == 'not-found' and fields['ActiveState'] == 'inactive' and pid == 0:
            return 0  # Transient systemd units disappear after reboot; the broker may initialize a stopped core.
        if result.returncode != 0 or fields['LoadState'] != 'loaded' or fields['ActiveState'] not in {'active', 'inactive', 'failed', 'activating', 'deactivating', 'reloading'}:
            raise DeploymentError('Native service state is unconfirmed')
        if pid and fields['ActiveState'] in {'inactive', 'failed'}:
            raise DeploymentError('Native service state is unconfirmed')
        return pid

    def stage(self, request, hardware_only=False):
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
        manifest = validate_archive(package, request["package_sha256"], build_hash, hardware_only)
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
        if stage['manifest'].get('ArtifactKind') == 'hardware-only':
            raise DeploymentError('Standalone hardware cannot enter program compilation')
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
            verify_candidate_exports(stage["manifest"], exports)
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
        if read_json(version / 'stage-result.json')['manifest'].get('ArtifactKind') == 'hardware-only':
            raise DeploymentError('Standalone hardware cannot activate a PLC program')
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
            verify_task_scheduling(connection, manifest)
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

    def apply_hardware(self, request):
        if "administrative_password" in request:
            request.pop("administrative_password", None)
            raise DeploymentError("Password transport is no longer supported; use the authorized local hardware broker")
        with self.connection() as connection:
            if connection.status() != "STOPPED":
                raise DeploymentError("Hardware apply requires PLC STOPPED; no implicit PLC stop is performed")
        active = self.active()
        self.verify_active(active, request.get("expected_active_build_hash"))
        version = self.version(request["build_hash"])
        old_core_pid = self.native_core_pid()
        bridge_units = ["stworkbench-is620n-axis.service"]
        if self.config.get("axis_v2_socket_path"):
            bridge_units.append(axis_gate.channel(axis_gate.INTERFACE_V2, self.config)['service'])
        previously_active = {unit for unit in bridge_units if subprocess.run(
            ["systemctl", "--user", "is-active", "--quiet", unit], capture_output=True, timeout=10).returncode == 0}
        stopped_units = []
        try:
            generated, hardware_manifest = scoped_transfer.inventory(version)
            ethercat_hardware.check_bundle(version, self.config)
            axis_manifest = generated / 'axis-manifest.json'
            raw = ethercat_hardware.read_regular(axis_manifest) if 'axis-manifest.json' in hardware_manifest['Files'] else None
            axes = scoped_transfer.validate_axes(axis_gate.strict_json(raw)) if raw is not None else None
            has_axes = bool(axes and axes['Axes'])
            interface = axes['InterfaceVersion'] if has_axes else axis_gate.INTERFACE
            selected = axis_gate.channel(interface, self.config)
            expected_socket = ("/home/a/.local/share/st-workbench/is620n-axis/axis.sock"
                               if interface == axis_gate.INTERFACE else selected['socket'])
            if has_axes and (selected['socket'] != expected_socket or self.config.get("axis_backend") != "EcMotion"):
                raise DeploymentError("Generic hardware application requires the configured private EcMotion axis bridge")
            def stop_bridge():
                for unit in bridge_units:
                    subprocess.run(["systemctl", "--user", "stop", unit], check=True, capture_output=True, timeout=20)
                    stopped_units.append(unit)
            applied = ethercat_hardware.apply_bundle(version, self.config, before_apply=stop_bridge, request_id=request["request_id"])
            if has_axes:
                destination = pathlib.Path(expected_socket).with_name("axis-manifest.json")
                if destination.is_symlink():
                    raise DeploymentError("Private axis manifest may not be a symlink")
                temporary = destination.with_name("axis-manifest-" + request["request_id"] + ".tmp")
                with open(temporary, "xb") as stream:
                    os.chmod(temporary, 0o600)
                    stream.write(raw)
                os.replace(temporary, destination)
                subprocess.run(["systemctl", "--user", "start", selected["service"]], check=True, capture_output=True, timeout=20)
                deadline = time.monotonic() + 10
                while True:
                    try:
                        loaded = (configuration_download.worker_digest(self.config, interface)
                                  if interface == axis_gate.INTERFACE_V2 else configuration_download.worker_digest(self.config))
                        if loaded != hashlib.sha256(raw).hexdigest():
                            raise ValueError('Loaded axis manifest differs from applied hardware')
                        break
                    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
                        if time.monotonic() >= deadline: raise
                        time.sleep(.1)
            ethercat_hardware.verify_applied(version / "core" / "generated", self.config)
        except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
            # A preflight rejection must not destroy the still-running
            # original channel. Only restore if the SAME old core lives;
            # never attach an old manifest to a changed/unknown process.
            if stopped_units and previously_active and old_core_pid:
                try:
                    if self.native_core_pid() == old_core_pid:
                        for unit in stopped_units:
                            if unit in previously_active:
                                subprocess.run(["systemctl", "--user", "start", unit], check=True, capture_output=True, timeout=20)
                except (OSError, subprocess.SubprocessError):
                    pass
            raise DeploymentError("Hardware apply failed or unconfirmed; query actual state before retry: " + str(error)) from error
        with self.connection() as connection:
            if connection.status() != "STOPPED":
                raise DeploymentError("Hardware applied but PLC state is unconfirmed")
        if self.active() != active:
            raise DeploymentError("Active PLC identity changed during hardware operation")
        atomic_json(self.prefix / "hardware-active.json", applied)
        return {"state": "hardware-applied", "active": active, "hardware": applied, "message": "总线组态已应用并取得核心指纹和实时反馈确认；PLC 保持停止，请重新编译设备候选后激活。"}

    def apply_configuration(self, request):
        if "administrative_password" in request:
            request.pop("administrative_password", None)
            raise DeploymentError("Password transport is not supported; use the authorized hardware broker")
        with self.connection() as connection:
            if connection.status() != "STOPPED":
                raise DeploymentError("Configuration apply requires PLC STOPPED; no implicit stop is performed")
        active = self.active()
        self.verify_active(active, request.get("expected_active_build_hash"))
        version = self.version(request["build_hash"])
        try:
            plan = configuration_download.plan(version, self.config)
            if plan["identity"] != request.get("configuration_identity"):
                raise ValueError("Configuration plan identity changed; confirm the actual plan before retry")
            if plan["kind"] == "axis":
                configuration_download.apply_axis(version, self.config, request["request_id"])
            elif plan["kind"] == "hardware":
                self.apply_hardware(request)
                configuration_download.wait_ready(version, self.config)
            else:
                configuration_download.wait_ready(version, self.config)
            with self.connection() as connection:
                if connection.status() != "STOPPED":
                    raise ValueError("Configuration applied but PLC stopped state is unconfirmed")
            self.verify_active(active, request.get("expected_active_build_hash"))
            if self.active() != active:
                raise ValueError("Active PLC identity changed during configuration update")
            _, manifest, compiled = configuration_download.candidate(version)
            if manifest.get("AxisInterfaceVersion") is not None:
                compiled["axis_ready"] = True
                compiled["axis_message"] = "Verified candidate axis manifest and advancing native feedback after configuration application"
                atomic_json(version / "compile-result.json", compiled)
        except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
            raise DeploymentError("Configuration apply failed or unconfirmed; query actual state before retry: " + str(error)) from error
        return {"state": "configuration-applied", "configuration_plan": plan, "active": active,
                "axis_ready": True, "message": "工程组态已匹配并验证，PLC 保持停止；可继续应用已编译程序。"}

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
            elif action == 'hardware-stage':
                result = self.stage(request, hardware_only=True)
            elif action in {'program-export', 'program-debug-map', 'hardware-export'}:
                try:
                    result = scoped_transfer.hardware(self) if action == 'hardware-export' else scoped_transfer.program(self, action == 'program-export')
                except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                    raise DeploymentError('Export could not be verified: ' + str(error)) from error
            elif action == "configuration-plan":
                try:
                    plan = configuration_download.plan(self.version(request["build_hash"]), self.config)
                except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                    raise DeploymentError("Configuration plan could not be verified: " + str(error)) from error
                result = {"state": "configuration-planned", "configuration_plan": plan}
            elif action == "configuration-apply":
                result = self.apply_configuration(request)
            elif action == "hardware-validate":
                try:
                    checked = ethercat_hardware.check_bundle(self.version(request["build_hash"]), self.config)
                except (OSError, ValueError, KeyError) as error:
                    raise DeploymentError("Hardware configuration rejected: " + str(error)) from error
                result = {"state": "hardware-validated", "message": "总线组态包检查通过；应用前必须停止 PLC 并撤销轴使能。", "hardware": checked}
            elif action == "hardware-apply":
                result = self.apply_hardware(request)
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
                        if action == "start":
                            self.verify_start_hardware(active)
                        result = {"state": connection.transition(action.upper(), "RUNNING" if action == "start" else "STOPPED").lower(), "active": active}
            else:
                raise DeploymentError("Unsupported explicit deployment action")
            if result.get("active") is not None:
                result["hardware_matches_active"] = self.active_hardware_matches(result["active"])
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
