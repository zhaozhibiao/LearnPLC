import copy
import json
import pathlib
import tempfile
import unittest
from unittest import mock
import zipfile

import coordinator
import motion_cycle
import test_coordinator


def bundle():
    return {"SchemaVersion": 1, "Executor": "EcMotion", "Configuration": {
        "Enabled": True, "PeriodMicroseconds": 1000, "SchedulingPolicy": "FIFO",
        "Priority": 80, "Cpu": None, "WatchdogCycles": 3, "AxisNames": ["X"]},
        "Axes": [{"Name": "X", "ControllerAxis": "Axis1", "Unit": "mm", "MinPosition": -100, "MaxPosition": 100}]}


class PackageTests(unittest.TestCase):
    setUp = test_coordinator.Tests.setUp
    tearDown = test_coordinator.Tests.tearDown
    package = test_coordinator.Tests.package
    request = test_coordinator.Tests.request
    def motion_package(self, data=None, flag="workbench-motion-cycle-v1", include=True):
        path, _, build = self.package({"motion-cycle.json": json.dumps(data or bundle()).encode()} if include else None)
        with zipfile.ZipFile(path) as archive:
            members = {name: archive.read(name) for name in archive.namelist()}
        manifest = json.loads(members["build-manifest.json"])
        if flag is not None:
            manifest["MotionCycleInterfaceVersion"] = flag
        members["build-manifest.json"] = json.dumps(manifest).encode()
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in members.items():
                archive.writestr(name, content)
        return path, coordinator.digest(path), build

    def test_motion_cycle_flag_required(self):
        path, checksum, build = self.motion_package(flag=None)
        with self.assertRaisesRegex(coordinator.DeploymentError, "motion.cycle"):
            coordinator.validate_archive(path, checksum, build)

    def test_motion_cycle_file_required(self):
        path, checksum, build = self.motion_package(include=False)
        with self.assertRaises(coordinator.DeploymentError):
            coordinator.validate_archive(path, checksum, build)

    def test_motion_cycle_valid(self):
        path, checksum, build = self.motion_package()
        self.assertEqual("workbench-motion-cycle-v1", coordinator.validate_archive(path, checksum, build)["MotionCycleInterfaceVersion"])

    def test_motion_cycle_rejects_more_than_64_axes(self):
        data = bundle()
        data["Configuration"]["AxisNames"] = ["Axis" + str(index) for index in range(65)]
        data["Axes"] = [{**data["Axes"][0], "Name": name, "ControllerAxis": name}
                        for name in data["Configuration"]["AxisNames"]]
        path, checksum, build = self.motion_package(data)
        with self.assertRaisesRegex(coordinator.DeploymentError, "axis group"):
            coordinator.validate_archive(path, checksum, build)

    def test_invalid_motion_cycle_schema_ranges_and_axes(self):
        changes = [("SchemaVersion", True), ("SchemaVersion", 2), ("Executor", "VirtualContinuous")]
        invalid = []
        for key, value in changes:
            data = bundle(); data[key] = value; invalid.append(data)
        for key, value in [("Enabled", False), ("PeriodMicroseconds", True), ("PeriodMicroseconds", 99),
                           ("PeriodMicroseconds", 100001), ("Priority", 90), ("Priority", 0),
                           ("Cpu", -1), ("Cpu", 1024), ("Cpu", False), ("WatchdogCycles", 0),
                           ("WatchdogCycles", 101), ("SchedulingPolicy", "OTHER"), ("AxisNames", ["X", "X"]),
                           ("AxisNames", ["Y"]), ("AxisNames", [])]:
            data = bundle(); data["Configuration"][key] = value; invalid.append(data)
        for key, value in [("ControllerAxis", ""), ("Unit", ""), ("MinPosition", float("nan")),
                           ("MaxPosition", float("inf")), ("MaxPosition", -100), ("MaximumVelocity", -1)]:
            data = bundle(); data["Axes"][0][key] = value; invalid.append(data)
        data = bundle(); data["Axes"].append(copy.deepcopy(data["Axes"][0])); invalid.append(data)
        data = bundle(); data["Axes"].append({**data["Axes"][0], "Name": "Y"}); data["Configuration"]["AxisNames"].append("Y"); invalid.append(data)
        for data in invalid:
            with self.subTest(data=data):
                path, checksum, build = self.motion_package(data)
                with self.assertRaises(coordinator.DeploymentError):
                    coordinator.validate_archive(path, checksum, build)

    def test_cycle_activation_refused_before_runtime_or_service_writes(self):
        active = self.runtime / "build/libplc_existing.so"; active.write_bytes(b"baseline")
        baseline = self.request("adopt-baseline")["active"]
        path, checksum, build = self.motion_package()
        self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)
        version = self.coordinator.version(build)
        (version / "build").mkdir(); library = version / "build/new_libplc.so"; library.write_bytes(b"candidate")
        coordinator.atomic_json(version / "compile-result.json", {"state": "compiled", "exports_verified": True, "library_sha256": coordinator.digest(library)})
        self.daemon.commands.clear()
        self.coordinator.config["motion_backend"] = "VirtualContinuous"
        with mock.patch.object(self.coordinator, "service") as service:
            with self.assertRaisesRegex(coordinator.DeploymentError, "adapter"):
                self.request("activate", build_hash=build, expected_active_build_hash=baseline["build_hash"])
            service.assert_not_called()
        self.assertEqual([], self.daemon.commands)
        self.assertEqual(b"baseline", active.read_bytes())
        self.assertFalse(self.coordinator.transaction_path.exists())

    def test_realtime_diagnostic_allowed_during_unfinished_transaction(self):
        coordinator.atomic_json(self.coordinator.transaction_path, {"state": "prepared"})
        result = self.request("realtime-status")
        self.assertTrue(result["success"])
        self.assertEqual("diagnostic", result["state"])
        self.assertFalse(result["realtime"]["MetricsAvailable"])
        self.assertEqual("ecmotion-rt.service", result["realtime"]["MotionService"]["Name"])
        self.assertEqual([], self.daemon.commands)

    def test_compile_reports_cycle_not_ready_even_with_virtual_backend(self):
        path, checksum, build = self.motion_package()
        self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)
        version = self.coordinator.version(build)
        (version / "build").mkdir(); (version / "build/new_libplc.so").write_bytes(b"candidate")
        self.coordinator.config["motion_backend"] = "VirtualContinuous"
        original = coordinator.read_json

        def read(path):
            return {"source_hashes": {}} if pathlib.Path(path).name == "runtime-profile.json" else original(path)

        def compile_command(args, directory, output, timeout):
            pathlib.Path(output).write_text("\n".join(coordinator.REQUIRED_EXPORTS) if args[0] == "nm" else "compiled")
            return 0

        with mock.patch.object(coordinator, "read_json", side_effect=read), mock.patch.object(coordinator, "run_bounded", side_effect=compile_command):
            result = self.request("compile", build_hash=build)
        self.assertEqual("compiled", result["state"])
        self.assertFalse(result["motion_cycle_ready"])
        self.assertIn("adapter", result["motion_cycle_message"])


class DiagnosticTests(unittest.TestCase):
    def test_reads_only_exact_service_cgroup_threads(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); group = root / "cgroup/workbench/child"; group.mkdir(parents=True)
            (group.parent / "cgroup.threads").write_text("101\n")
            (group / "cgroup.threads").write_text("102\n101\n")
            for tid in (101, 102):
                process = root / "proc" / str(tid); process.mkdir(parents=True)
                (process / "comm").write_text("plc-scan\n")
                (process / "status").write_text("Name:\tplc-scan\nCpus_allowed_list:\t2-3\n")
            rt = root / "realtime"; rt.write_text("1\n")
            properties = {"ActiveState": "active", "ControlGroup": "/workbench"}
            with mock.patch.object(motion_cycle, "_unit_properties", return_value=properties) as units, \
                    mock.patch.object(motion_cycle.os, "sched_getscheduler", return_value=1, create=True), \
                    mock.patch.object(motion_cycle.os, "sched_getparam", return_value=type("Param", (), {"sched_priority": 40})(), create=True):
                result = motion_cycle.realtime_status(root / "proc", root / "cgroup", rt)
            self.assertEqual([mock.call("stworkbench-openplc.service", True), mock.call("ecmotion-rt.service", False)], units.call_args_list)
            self.assertTrue(result["RealtimeKernel"])
            self.assertEqual([101, 102], [item["Tid"] for item in result["PlcThreads"]])
            self.assertEqual({"Name": "plc-scan", "Tid": 101, "Policy": "SCHED_FIFO", "Priority": 40, "Cpus": [2, 3]}, result["PlcThreads"][0])

    def test_unknown_kernel_and_unavailable_services_do_not_invent_statistics(self):
        with mock.patch.object(motion_cycle, "_unit_properties", side_effect=OSError("unavailable")):
            result = motion_cycle.realtime_status(realtime_path="missing-realtime-flag")
        self.assertIsNone(result["RealtimeKernel"])
        self.assertEqual([], result["PlcThreads"])
        self.assertEqual("unknown", result["MotionService"]["State"])
        self.assertFalse(result["MetricsAvailable"])

    def test_thread_count_and_reads_are_bounded(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); group = root / "workbench"; group.mkdir()
            (group / "cgroup.threads").write_text("\n".join(str(value) for value in range(1, 1000)))
            with mock.patch.object(motion_cycle, "_unit_properties", return_value={"ActiveState": "active", "ControlGroup": "/workbench"}), \
                    mock.patch.object(motion_cycle, "_thread", side_effect=lambda tid, proc: {"Tid": tid}):
                service = motion_cycle._service("ecmotion-rt.service", False, root, root)
            self.assertEqual(256, len(service["Threads"]))
            huge = root / "huge"; huge.write_bytes(b"x" * 9000)
            with self.assertRaises(ValueError):
                motion_cycle._read(huge)


if __name__ == "__main__":
    unittest.main()
