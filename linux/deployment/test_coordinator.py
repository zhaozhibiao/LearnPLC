import hashlib
import json
import pathlib
import socket
import tempfile
import threading
import unittest
import zipfile
import coordinator


class Daemon:
    def __init__(self, directory, state="STOPPED"):
        self.path = str(pathlib.Path(directory) / "daemon.sock") if hasattr(socket, "AF_UNIX") else ("127.0.0.1", 0)
        self.state = state
        self.commands = []
        self.server = socket.socket(socket.AF_UNIX if hasattr(socket, "AF_UNIX") else socket.AF_INET, socket.SOCK_STREAM)
        self.server.bind(self.path)
        if isinstance(self.path, tuple):
            self.path = self.server.getsockname()
        self.server.listen(8)
        self.server.settimeout(.1)
        self.alive = True
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while self.alive:
            try:
                client, _ = self.server.accept()
            except socket.timeout:
                continue
            with client:
                reader = client.makefile("rb")
                while self.alive:
                    command = reader.readline()
                    if not command:
                        break
                    self.commands.append(command)
                    if command == b"STATUS\n":
                        response = "STATUS:" + self.state
                    elif command == b"START\n":
                        self.state = "RUNNING"
                        response = "START:OK"
                    elif command == b"STOP\n":
                        self.state = "STOPPED"
                        response = "STOP:OK"
                    else:
                        response = "COMMAND:ERROR"
                    client.sendall((response + "\n").encode("ascii"))

    def close(self):
        self.alive = False
        self.thread.join(1)
        self.server.close()


class Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="st-deploy-")
        self.root = pathlib.Path(self.temp.name)
        self.runtime = self.root / "runtime"
        (self.runtime / "build").mkdir(parents=True)
        self.daemon = Daemon(self.temp.name)
        self.coordinator = coordinator.Coordinator({"prefix": str(self.root / "deployment"), "runtime_source": str(self.runtime),
            "socket_path": self.daemon.path, "exclusive_control": True, "runtime_commit": coordinator.RUNTIME_COMMIT})

    def tearDown(self):
        self.daemon.close()
        self.temp.cleanup()

    def package(self, extras=None):
        build_hash = "A" * 64
        files = {"generated.hpp": b"header", "generated.cpp": b"source", "generated_debug.cpp": b"debug", "debug-map.json": b"{}",
                 "defines.h": b"md5", "configuration.st": b"config", "strucpp_runtime/include/debug_table.hpp": b"types"}
        if extras:
            files.update(extras)
        manifest = {"BuildHash": build_hash, "RuntimeCommit": coordinator.RUNTIME_COMMIT, "CompilerVersion": "STruC++ version 0.7.0",
                    "ProgramMd5": "a" * 32, "SymbolTableHash": hashlib.sha256(b"{}").hexdigest().upper(), "HardwareHash": "hardware",
                    "Files": {name: hashlib.sha256(content).hexdigest().upper() for name, content in files.items()}}
        path = self.root / "candidate.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in files.items():
                archive.writestr(name, content)
            archive.writestr("build-manifest.json", json.dumps(manifest))
        return path, hashlib.sha256(path.read_bytes()).hexdigest(), build_hash

    def request(self, action, **kwargs):
        return self.coordinator.execute({"action": action, "request_id": "test-" + action, **kwargs})

    def test_exact_ipc_encoding(self):
        with coordinator.RuntimeConnection(self.daemon.path) as connection:
            self.assertEqual("STOPPED", connection.status())
        self.assertEqual([b"STATUS\n"], self.daemon.commands)

    def test_offline_status_retains_transaction_evidence(self):
        coordinator.atomic_json(self.coordinator.transaction_path, {"state": "prepared"})
        self.coordinator.connection = lambda: (_ for _ in ()).throw(coordinator.DeploymentError("Offline"))
        result = self.request("status")
        self.assertEqual("unavailable", result["state"])
        self.assertEqual("prepared", result["transaction"]["state"])
        self.assertFalse(result["runtime_confirmed"])

    def test_terminal_recovery_reconciles_service(self):
        active_path = self.runtime / "build" / "libplc_existing.so"
        active_path.write_bytes(b"baseline")
        active = self.request("adopt-baseline")["active"]
        coordinator.atomic_json(self.coordinator.transaction_path, {"state": "committed", "new_active": active})
        resumed = []
        self.coordinator.resume_service = lambda allow_empty=False: resumed.append(allow_empty)
        result = self.request("recover")
        self.assertEqual("committed", result["state"])
        self.assertEqual([False], resumed)

    def test_archive_inventory_validated(self):
        path, digest, identity = self.package()
        manifest = coordinator.validate_archive(path, digest, identity)
        self.assertEqual(identity, manifest["BuildHash"])

    def test_traversal_rejected(self):
        path, digest, identity = self.package({"../escape.cpp": b"bad"})
        with self.assertRaises(coordinator.DeploymentError):
            coordinator.validate_archive(path, digest, identity)

    def test_stage_does_not_touch_active_or_start(self):
        active = self.runtime / "build" / "libplc_existing.so"
        active.write_bytes(b"baseline")
        path, digest, identity = self.package()
        result = self.request("stage", package_path=str(path), package_sha256=digest, build_hash=identity)
        self.assertEqual("staged", result["state"])
        self.assertEqual(b"baseline", active.read_bytes())
        self.assertEqual([], self.daemon.commands)

    def test_activation_without_baseline_refused(self):
        with self.assertRaises(coordinator.DeploymentError):
            self.request("activate", build_hash="A" * 64, expected_active_build_hash=None)

    def test_restage_identical_content_with_different_zip_metadata(self):
        path, checksum, build = self.package()
        self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)
        original = coordinator.read_json(self.coordinator.version(build) / "stage-result.json")
        with zipfile.ZipFile(path) as archive:
            members = [(name, archive.read(name)) for name in archive.namelist()]
        repacked = self.root / "repacked.zip"
        with zipfile.ZipFile(repacked, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, content in reversed(members):
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                archive.writestr(entry, content, compress_type=zipfile.ZIP_DEFLATED)
        alternate = coordinator.digest(repacked)
        self.assertNotEqual(checksum, alternate)
        self.assertEqual("staged", self.request("stage", package_path=str(repacked), package_sha256=alternate, build_hash=build)["state"])
        self.assertEqual(original, coordinator.read_json(self.coordinator.version(build) / "stage-result.json"))
        self.assertEqual([], self.daemon.commands)

    def test_restage_same_build_identity_different_contents_rejected(self):
        path, checksum, build = self.package()
        self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)
        path, checksum, build = self.package({"generated.cpp": b"changed source"})
        with self.assertRaises(coordinator.DeploymentError):
            self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)

    def test_restage_refuses_corrupted_existing_sources_even_same_archive(self):
        path, checksum, build = self.package()
        self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)
        (self.coordinator.version(build) / "core/generated/generated.cpp").write_bytes(b"tampered")
        with self.assertRaises(coordinator.DeploymentError):
            self.request("stage", package_path=str(path), package_sha256=checksum, build_hash=build)

    def test_empty_baseline_requires_exact_empty_and_no_library(self):
        # This fixture has no safe-mode install proof; bare STOPPED is insufficient.
        with self.assertRaises(coordinator.DeploymentError):
            self.request("adopt-empty-baseline")
        self.daemon.state = "EMPTY"
        result = self.request("adopt-empty-baseline")
        self.assertEqual("baseline-empty", result["state"])
        self.assertIsNone(result["active"]["build_hash"])

    def test_safe_mode_stopped_empty_baseline(self):
        self.coordinator.config["safe_mode_verified"] = True
        result = self.request("adopt-empty-baseline")
        self.assertEqual("baseline-empty", result["state"])
        self.assertIsNone(result["active"]["build_hash"])
        self.assertNotIn(b"START\n", self.daemon.commands)

    def test_pending_transaction_refuses_new_changes(self):
        coordinator.atomic_json(self.coordinator.transaction_path, {"state": "prepared"})
        path, digest, identity = self.package()
        with self.assertRaises(coordinator.DeploymentError):
            self.request("stage", package_path=str(path), package_sha256=digest, build_hash=identity)

    def test_active_checksum_change_refuses_activation(self):
        identity = self.prepare_candidate()
        active = self.runtime / "build" / "libplc_existing.so"
        active.write_bytes(b"baseline")
        baseline = self.request("adopt-baseline")["active"]
        active.write_bytes(b"unexpected-change")
        with self.assertRaises(coordinator.DeploymentError):
            self.request("activate", build_hash=identity, expected_active_build_hash=baseline["build_hash"])
        self.assertEqual(b"unexpected-change", active.read_bytes())

    def test_explicit_start_and_stop_are_separate_actions(self):
        library = self.runtime / "build" / "libplc_existing.so"
        library.write_bytes(b"baseline")
        active = self.request("adopt-baseline")["active"]
        self.assertEqual("running", self.request("start", expected_active_build_hash=active["build_hash"])["state"])
        self.assertEqual("stopped", self.request("stop", expected_active_build_hash=active["build_hash"])["state"])
        self.assertEqual(1, self.daemon.commands.count(b"START\n"))
        self.assertEqual(1, self.daemon.commands.count(b"STOP\n"))

    def test_service_barrier_surrounds_atomic_activation(self):
        events = []
        class ServiceFixture(coordinator.Coordinator):
            def connection(inner):
                return coordinator.RuntimeConnection(self.daemon.path)
            def service(inner, action):
                events.append(action)
                if action == "stop":
                    inner.inactive_service()
            def inactive_service(inner):
                self.assertEqual("stop", events[-1])
            def resume_service(inner, allow_empty=False):
                self.assertEqual("committed", coordinator.read_json(inner.transaction_path)["state"])
                self.assertEqual(b"candidate-library", (self.runtime / "build" / "libplc_existing.so").read_bytes())
                events.append("start-safe-mode-service")
        self.coordinator = ServiceFixture(self.coordinator.config)
        identity = self.prepare_candidate()
        library = self.runtime / "build" / "libplc_existing.so"
        library.write_bytes(b"baseline")
        active = self.request("adopt-baseline")["active"]
        self.coordinator.config["control_transport"] = "rest"
        self.request("activate", build_hash=identity, expected_active_build_hash=active["build_hash"])
        self.assertEqual(["stop", "start-safe-mode-service"], events)
        self.assertNotIn(b"START\n", self.daemon.commands)

    def prepare_candidate(self):
        path, digest, identity = self.package()
        self.request("stage", package_path=str(path), package_sha256=digest, build_hash=identity)
        version = pathlib.Path(self.coordinator.config["prefix"]) / ".versions" / identity
        (version / "build").mkdir()
        library = version / "build" / "new_libplc.so"
        library.write_bytes(b"candidate-library")
        (version / "compile-result.json").write_text(json.dumps({"state": "compiled", "build_hash": identity,
            "library_sha256": hashlib.sha256(library.read_bytes()).hexdigest(), "exports_verified": True}))
        return identity

    def test_explicit_activation_preserves_baseline_and_never_starts(self):
        identity = self.prepare_candidate()
        active = self.runtime / "build" / "libplc_existing.so"
        active.write_bytes(b"baseline")
        baseline = self.request("adopt-baseline")["active"]
        result = self.request("activate", build_hash=identity, expected_active_build_hash=baseline["build_hash"])
        self.assertEqual("activated-not-running", result["state"])
        self.assertEqual(b"candidate-library", active.read_bytes())
        self.assertNotIn(b"START\n", self.daemon.commands)
        history = pathlib.Path(self.coordinator.config["prefix"]) / ".history"
        self.assertTrue(any(path.read_bytes() == b"baseline" for path in history.glob("*.so")))

    def test_running_daemon_refuses_activation(self):
        identity = self.prepare_candidate()
        active = self.runtime / "build" / "libplc_existing.so"
        active.write_bytes(b"baseline")
        baseline = self.request("adopt-baseline")["active"]
        self.daemon.state = "RUNNING"
        with self.assertRaises(coordinator.DeploymentError):
            self.request("activate", build_hash=identity, expected_active_build_hash=baseline["build_hash"])
        self.assertEqual(b"baseline", active.read_bytes())


if __name__ == "__main__":
    unittest.main()
