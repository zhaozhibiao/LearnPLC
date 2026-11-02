"""Motion-cycle package validation and bounded, read-only Linux diagnostics.

There is no physical EcMotion execution adapter in this first-stage interface.
"""
import math
import os
import pathlib
import re
import subprocess
import threading

INTERFACE = "workbench-motion-cycle-v1"
ADAPTER_MESSAGE = "Physical EcMotion execution adapter is unavailable; motion-cycle activation is not supported"
MAX_THREADS = 256
MAX_CGROUPS = 128


def validate_configuration(data):
    def require(condition, message):
        if not condition:
            raise ValueError("Invalid motion-cycle configuration: " + message)

    def integer(value, lower, upper):
        return type(value) is int and lower <= value <= upper

    def number(value):
        return type(value) in (int, float) and math.isfinite(value)

    def text(value):
        return isinstance(value, str) and bool(value.strip()) and len(value) <= 256

    require(isinstance(data, dict), "object required")
    require(set(data) == {"SchemaVersion", "Executor", "Configuration", "Axes"}, "schema fields")
    require(type(data.get("SchemaVersion")) is int and data["SchemaVersion"] == 1, "schema version")
    require(data.get("Executor") == "EcMotion", "executor")
    config = data.get("Configuration")
    require(isinstance(config, dict), "Configuration object required")
    require(set(config) == {"Enabled", "PeriodMicroseconds", "SchedulingPolicy", "Priority", "Cpu", "WatchdogCycles", "AxisNames"}, "configuration fields")
    require(config.get("Enabled") is True, "enabled flag")
    for field, lower, upper in (("PeriodMicroseconds", 100, 100000), ("Priority", 60, 89), ("WatchdogCycles", 1, 100)):
        require(integer(config.get(field), lower, upper), field + " range")
    require(config.get("SchedulingPolicy") == "FIFO", "FIFO policy required")
    require("Cpu" in config and (config["Cpu"] is None or integer(config["Cpu"], 0, 1023)), "Cpu range")
    names = config.get("AxisNames")
    require(isinstance(names, list) and 0 < len(names) <= 64 and all(text(name) for name in names), "axis group")
    require(len({name.casefold() for name in names}) == len(names), "duplicate axis names")
    axes = data.get("Axes")
    require(isinstance(axes, list) and len(axes) == len(names), "axis entries must match selected names")
    axis_names, controllers = set(), set()
    for axis in axes:
        require(isinstance(axis, dict), "axis object required")
        required = {"Name", "ControllerAxis", "Unit", "MinPosition", "MaxPosition"}
        caps = {"MaximumVelocity", "MaximumAcceleration", "MaximumDeceleration", "MaximumJerk"}
        require(required.issubset(axis) and set(axis).issubset(required | caps), "axis schema fields")
        name = axis.get("Name")
        require(text(name) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name), "axis name")
        require(name.casefold() not in axis_names, "duplicate axis entry")
        axis_names.add(name.casefold())
        controller = axis.get("ControllerAxis")
        require(text(controller) and controller.casefold() not in controllers, "controller axis missing or duplicate")
        controllers.add(controller.casefold())
        require(text(axis.get("Unit")), "axis unit")
        require(number(axis.get("MinPosition")) and number(axis.get("MaxPosition")) and axis["MinPosition"] < axis["MaxPosition"], "finite ordered axis limits")
        for field in ("MaximumVelocity", "MaximumAcceleration", "MaximumDeceleration", "MaximumJerk"):
            if field in axis and axis[field] is not None:
                require(number(axis[field]) and axis[field] > 0, "positive finite " + field)
    require({axis["Name"] for axis in axes} == set(names), "axis entries must exactly match selected names")
    return data


def _read(path, limit=8192):
    with open(path, "rb") as stream:
        content = stream.read(limit + 1)
    if len(content) > limit:
        raise ValueError("Diagnostic file exceeds bounded read")
    return content.decode("utf8", errors="replace")


def _unit_properties(name, user):
    # Only these two non-sensitive properties are read; never Environment/ExecStart.
    arguments = ["systemctl"] + (["--user"] if user else []) + ["show", name, "--property=ActiveState", "--property=ControlGroup"]
    process = subprocess.Popen(arguments, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    output = []
    def read_output():
        output.append(process.stdout.read(8193))
        if len(output[0]) > 8192:
            process.kill()
    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    try:
        code = process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=1)
        raise
    finally:
        reader.join(1)
        if not reader.is_alive():
            process.stdout.close()
    if reader.is_alive() or code != 0 or not output:
        raise ValueError("Service diagnostics unavailable")
    raw = output[0]
    if len(raw) > 8192:
        raise ValueError("Service diagnostic response exceeds bounded read")
    return dict(line.split("=", 1) for line in raw.decode("utf8", errors="replace").splitlines() if "=" in line)


def _thread(tid, proc_root):
    process = pathlib.Path(proc_root) / str(tid)
    name = _read(process / "comm", 256).strip()
    status = _read(process / "status", 65536)
    affinity = next((line.split(":", 1)[1].strip() for line in status.splitlines() if line.startswith("Cpus_allowed_list:")), "")
    cpus = []
    for segment in affinity.split(","):
        if not re.fullmatch(r"\d{1,5}(?:-\d{1,5})?", segment):
            continue
        bounds = [int(value) for value in segment.split("-")]
        lower, upper = bounds[0], bounds[-1]
        if 0 <= lower <= upper <= 65535 and len(cpus) + upper - lower + 1 <= 4096:
            cpus.extend(range(lower, upper + 1))
    policy = os.sched_getscheduler(tid)
    labels = {0: "SCHED_OTHER", 1: "SCHED_FIFO", 2: "SCHED_RR", 3: "SCHED_BATCH", 5: "SCHED_IDLE", 6: "SCHED_DEADLINE"}
    return {"Name": name, "Tid": tid, "Policy": labels.get(policy, "UNKNOWN"),
            "Priority": os.sched_getparam(tid).sched_priority, "Cpus": cpus}


def _service(name, user, proc_root, cgroup_root):
    service = {"Name": name, "State": "unknown", "Threads": []}
    try:
        properties = _unit_properties(name, user)
        service["State"] = properties.get("ActiveState", "unknown")
        group = properties.get("ControlGroup", "")
        if not group:
            return service
        relative = pathlib.PurePosixPath(group)
        if not group.startswith("/") or ".." in relative.parts:
            raise ValueError("Invalid service cgroup")
        root = pathlib.Path(cgroup_root).resolve()
        # Unified cgroup v2, or systemd's v1 hierarchy. No whole-system /proc scan.
        directory = (root / group.lstrip("/")).resolve()
        if not directory.is_dir():
            directory = (root / "systemd" / group.lstrip("/")).resolve()
        directory.relative_to(root)
        queue, tids, visited = [directory], set(), 0
        while queue and visited < MAX_CGROUPS and len(tids) < MAX_THREADS:
            current = queue.pop(0); visited += 1
            source = current / "cgroup.threads"
            if not source.exists():
                source = current / "tasks"
            for value in _read(source, 65536).splitlines():
                if value.isdecimal() and 0 < int(value) <= 2147483647:
                    tids.add(int(value))
                if len(tids) >= MAX_THREADS:
                    break
            with os.scandir(current) as entries:
                for entry in entries:
                    if len(queue) + visited >= MAX_CGROUPS:
                        break
                    if entry.is_dir(follow_symlinks=False):
                        queue.append(pathlib.Path(entry.path))
        for tid in sorted(tids):
            try:
                service["Threads"].append(_thread(tid, proc_root))
            except (OSError, ValueError, AttributeError):
                # Threads may exit or be inaccessible between the two reads.
                continue
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return service


def realtime_status(proc_root="/proc", cgroup_root="/sys/fs/cgroup", realtime_path="/sys/kernel/realtime"):
    realtime = None
    try:
        value = _read(realtime_path, 32).strip()
        realtime = {"1": True, "0": False}.get(value)
    except (OSError, ValueError):
        pass
    release = os.uname().release if hasattr(os, "uname") else "unknown"
    plc = _service("stworkbench-openplc.service", True, proc_root, cgroup_root)
    motion = _service("ecmotion-rt.service", False, proc_root, cgroup_root)
    return {"KernelRelease": release, "RealtimeKernel": realtime, "CpuCount": os.cpu_count(),
            "PlcThreads": plc["Threads"], "MotionService": motion, "MetricsAvailable": False,
            "Message": ADAPTER_MESSAGE + "; actual cycle/jitter statistics are unavailable. Thread observations are bounded and may be incomplete."}
