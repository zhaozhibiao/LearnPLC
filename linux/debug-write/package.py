"""Create a reviewable patch/provenance, or verify and install into a source copy.

Does not build, restart, contact a controller, or modify the staged reference.
"""
import argparse
import difflib
import hashlib
import json
from pathlib import Path
import shutil

HERE = Path(__file__).resolve().parent
FILES = ("debug_handler.c", "debug_handler.h", "image_tables.cpp", "plc_state_manager.cpp", "workbench_task_scheduling.h")
REL = Path("core/src/plc_app")
REFERENCE = HERE.parents[1] / "artifacts/upstream/openplc-runtime-v4.3.0"

def digest(data):
    return hashlib.sha256(data).hexdigest()

def make_package():
    patch = []
    manifest = {"upstream": "OpenPLC Runtime v4.3.0", "license": "MIT (upstream SPDX/copyright retained)", "files": {}}
    for name in FILES:
        reference_file = REFERENCE / REL / name
        before = reference_file.read_bytes() if reference_file.exists() else b""
        after = (HERE / "src" / name).read_bytes()
        manifest["files"][str(REL / name).replace("\\", "/")] = {"before_sha256": digest(before) if reference_file.exists() else None, "after_sha256": digest(after)}
        patch.extend(difflib.unified_diff(before.decode().splitlines(True), after.decode().splitlines(True), fromfile="a/" + REL.as_posix() + "/" + name if reference_file.exists() else "/dev/null", tofile="b/" + REL.as_posix() + "/" + name))
    # Normalize patch line endings; provenance hashes remain actual byte hashes.
    (HERE / "debug-write-v1.patch").write_bytes("".join(patch).replace("\r\n", "\n").encode())
    (HERE / "provenance.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

def install(root):
    root = root.resolve()
    if root == REFERENCE.resolve():
        raise SystemExit("Refusing to modify read-only upstream reference; use a build copy")
    manifest = json.loads((HERE / "provenance.json").read_text(encoding="utf-8"))
    pending = []
    # Validate every file first, so mismatched revisions cause no partial install.
    for rel, hashes in manifest["files"].items():
        target = root / rel
        source = HERE / "src" / Path(rel).name
        if digest(source.read_bytes()) != hashes["after_sha256"]:
            raise SystemExit("Packaged source hash mismatch: " + str(source))
        current = digest(target.read_bytes()) if target.exists() else None
        if current not in hashes.values():
            raise SystemExit("Source revision mismatch: " + str(target))
        if current == hashes["before_sha256"]:
            pending.append((source, target))
    for source, target in pending:
        shutil.copyfile(source, target)
    print(f"Verified {len(FILES)} source files; installed {len(pending)}. Rebuild runtime executable separately.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, help="Runtime source build copy root (no service actions)")
    args = parser.parse_args()
    if args.install:
        install(args.install)
    else:
        make_package()
