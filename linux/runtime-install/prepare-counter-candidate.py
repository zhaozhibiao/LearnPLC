"""Prepare only the checksum-pinned, hardware-free counter smoke fixture in an isolated candidate tree."""
import hashlib
import pathlib
import shutil
import stat
import zipfile

PREFIX = pathlib.Path.home() / '.local/share/st-workbench/runtime-v4.3.0'
EXPECTED = 'd2f714ecdb07a305579a83b4b7ce10adf6741cfeb24ce91e18f3897471c53727'

def prepare(package):
    if str(PREFIX.resolve()) != '/home/a/.local/share/st-workbench/runtime-v4.3.0':
        raise ValueError('Unexpected installation prefix')
    if hashlib.sha256(package.read_bytes()).hexdigest() != EXPECTED:
        raise ValueError('Counter fixture checksum differs')
    destination = PREFIX / 'candidates' / EXPECTED / 'work'
    destination.mkdir(parents=True, mode=0o700)
    source = PREFIX / 'source'
    shutil.copytree(source / 'core/strucpp_runtime', destination / 'core/strucpp_runtime')
    shutil.copytree(source / 'core/src/plc_app', destination / 'core/src/plc_app')
    shutil.copytree(source / 'core/src/lib', destination / 'core/src/lib')
    (destination / 'scripts').mkdir()
    for name in ('compile.sh', 'Makefile.strucpp'):
        (destination / 'scripts' / name).write_text((source / 'scripts' / name).read_text(encoding='utf-8'), encoding='utf-8', newline='\n')
    generated = destination / 'core/generated'
    generated.mkdir(mode=0o700)
    with zipfile.ZipFile(package) as archive:
        for member in archive.infolist():
            output = generated / member.filename
            if member.is_dir() or stat.S_ISLNK(member.external_attr >> 16):
                raise ValueError('Unsupported archive entry')
            output.resolve().relative_to(generated.resolve())
            if member.file_size > 10 * 1024 * 1024:
                raise ValueError('Archive entry exceeds size bound')
            output.parent.mkdir(parents=True, exist_ok=True)
            with output.open('xb') as stream:
                stream.write(archive.read(member))
    print(destination)

if __name__ == '__main__':
    import sys
    prepare(pathlib.Path(sys.argv[1]))
