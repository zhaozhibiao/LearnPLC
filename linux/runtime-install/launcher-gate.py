"""Launch guard only; never activates, changes a journal, or starts a PLC program."""
import hashlib
import json
import pathlib
import re
REVISION='a9e38d901fb87deb5a8cf778b52639b9387834d0'

def record(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size>1024*1024:
        raise ValueError('Deployment record is invalid')
    try: value=json.loads(path.read_text(encoding='utf-8'))
    except (ValueError,UnicodeError): raise ValueError('Deployment record JSON is invalid') from None
    if not isinstance(value,dict): raise ValueError('Deployment record must be an object')
    return value

def validate(prefix):
    prefix=pathlib.Path(prefix)
    deployment=prefix/'deployment'
    build=prefix/'source/build'
    if deployment.is_symlink() or build.is_symlink(): raise ValueError('Deployment/build directory may not be a symbolic link')
    # Match the pinned native loader's filename selector exactly, including
    # suffixes such as .so.old that a normal extension glob would overlook.
    libraries=[entry for entry in build.iterdir() if entry.name.startswith('libplc_') and '.so' in entry.name]
    active_path=deployment/'active.json'
    transaction_path=deployment/'transaction.json'
    active=record(active_path) if active_path.exists() or active_path.is_symlink() else None
    if transaction_path.exists() or transaction_path.is_symlink():
        transaction=record(transaction_path)
        state=transaction.get('state')
        if state not in ('committed','rolled-back'): raise ValueError('Unfinished deployment transaction blocks startup')
        expected=transaction.get('new_active' if state=='committed' else 'old_active')
        if active is None or active!=expected: raise ValueError('Deployment transaction does not match active identity')
    if active is None:
        if libraries: raise ValueError('Unapproved program library blocks startup')
        return
    if active.get('runtime_commit')!=REVISION: raise ValueError('Active Runtime revision differs')
    library_path=active.get('library_path')
    digest=active.get('library_sha256')
    if active.get('build_hash') is None and digest is None:
        reserved=build/'libplc_stworkbench.so'
        if library_path is not None and library_path!=str(reserved):
            raise ValueError('Empty active identity has an unexpected reserved target')
        if libraries: raise ValueError('Empty active identity contains a program library')
        return
    if not isinstance(library_path,str) or not isinstance(digest,str) or not re.fullmatch('[0-9a-f]{64}',digest):
        raise ValueError('Active artifact identity is incomplete')
    library=pathlib.Path(library_path)
    if not library.is_absolute() or library.parent.resolve()!=build.resolve() or not re.fullmatch(r'libplc_[A-Za-z0-9_.-]+\.so',library.name):
        raise ValueError('Active library is outside the reviewed Runtime build directory')
    if library.is_symlink() or not library.is_file() or len(libraries)!=1 or libraries[0].resolve()!=library.resolve():
        raise ValueError('Active library set differs from the committed manifest')
    if library.stat().st_size>100*1024*1024: raise ValueError('Active library exceeds startup verification bound')
    hasher=hashlib.sha256()
    with library.open('rb') as stream:
        for chunk in iter(lambda:stream.read(65536),b''): hasher.update(chunk)
    if hasher.hexdigest()!=digest: raise ValueError('Active library checksum differs from committed identity')

if __name__=='__main__':
    import sys
    try:
        expected=pathlib.Path.home()/'.local/share/st-workbench/runtime-v4.3.0'
        if str(expected.resolve())!='/home/a/.local/share/st-workbench/runtime-v4.3.0': raise ValueError('Unexpected Runtime prefix')
        validate(expected)
    except (ValueError,OSError) as error:
        print('Runtime startup refused: '+str(error),file=sys.stderr)
        raise SystemExit(1)
