#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Compile a candidate under a new /tmp directory; never load or activate it."""
import argparse
import hashlib
import json
import pathlib
import subprocess
import tempfile
import zipfile


def main():
    parser=argparse.ArgumentParser();parser.add_argument('package');parser.add_argument('--runtime-source',required=True);args=parser.parse_args()
    import coordinator
    package=pathlib.Path(args.package);source=pathlib.Path(args.runtime_source)
    with zipfile.ZipFile(package) as archive: build=json.loads(archive.read('build-manifest.json'))['BuildHash']
    manifest=coordinator.validate_archive(package,coordinator.digest(package),build)
    root=pathlib.Path(tempfile.mkdtemp(prefix='st-communication-compile-',dir='/tmp'))
    generated=root/'core/generated';generated.mkdir(parents=True)
    with zipfile.ZipFile(package) as archive:
        for name in archive.namelist():
            target=generated/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(archive.read(name))
    profile=json.loads(pathlib.Path(coordinator.__file__).with_name('runtime-profile.json').read_text())
    for name,checksum in profile['source_hashes'].items():
        content=(source/name).read_bytes().replace(b'\r\n',b'\n')
        if hashlib.sha256(coordinator.canonical_pinned_source(name,content)).hexdigest().upper()!=checksum:
            raise ValueError('Runtime source pin differs: '+name)
        target=root/name;target.parent.mkdir(parents=True,exist_ok=True)
        target.write_bytes(coordinator.motion_candidate_source(name,content,manifest))
    log=root/'compile.log'
    with log.open('w') as stream:
        result=subprocess.run(['make','-j2','-f','scripts/Makefile.strucpp'],cwd=root,stdout=stream,stderr=subprocess.STDOUT,timeout=300)
    if result.returncode:
        print(log.read_text()[-10000:]);raise SystemExit(result.returncode)
    library=root/'build/new_libplc.so'
    exports=subprocess.check_output(['nm','-D','--defined-only',str(library)],text=True)
    missing=coordinator.REQUIRED_EXPORTS-{line.split()[-1] for line in exports.splitlines() if line.split()}
    if missing:raise ValueError('Runtime exports missing: '+','.join(sorted(missing)))
    print(json.dumps(dict(Success=True,BuildHash=build,LibrarySha256=coordinator.digest(library),Directory=str(root),Activated=False,Loaded=False)))


if __name__=='__main__':main()
