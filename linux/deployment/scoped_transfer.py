"""MIT. Bounded, read-only export of verified active program/applied hardware."""
import hashlib
import pathlib
import os
import stat
import json
import ethercat_hardware as hw
import configuration_download as configuration
import axis_gate

MAX_TRANSFER = 20 * 1024 * 1024


def read_archive(path):
    path = pathlib.Path(path).absolute()
    for parent in (path, *path.parents):
        if parent.is_symlink(): raise ValueError('Symlink in source archive path')
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > 16 * 1024 * 1024: raise ValueError('Source archive exceeds capacity')
        raw = stream.read(16 * 1024 * 1024 + 1)
        if len(raw) > 16 * 1024 * 1024: raise ValueError('Source archive exceeds capacity')
        return raw


def validate_axes(data):
    if isinstance(data, dict) and data.get('Axes') == []:
        if (set(data) != {'SchemaVersion','InterfaceVersion','PlcIntervalMilliseconds','Axes'}
                or type(data['SchemaVersion']) is not int or data['SchemaVersion'] != 1
                or data['InterfaceVersion'] != axis_gate.INTERFACE or type(data['PlcIntervalMilliseconds']) is not int
                or not 1 <= data['PlcIntervalMilliseconds'] <= 1000):
            raise ValueError('Invalid empty hardware axis manifest')
        return data
    return axis_gate.validate_manifest(data)


def inventory(version):
    generated = pathlib.Path(version) / 'core' / 'generated'
    manifest = hw.parse_json(hw.read_regular(generated / 'build-manifest.json'))
    stage = hw.parse_json(hw.read_regular(pathlib.Path(version) / 'stage-result.json'))
    if stage.get('manifest') != manifest or hw.hex_identity(manifest.get('BuildHash')) != hw.hex_identity(pathlib.Path(version).name):
        raise ValueError('Staged inventory identity differs')
    files = manifest.get('Files')
    if not isinstance(files, dict) or not 1 <= len(files) <= 1024: raise ValueError('Invalid inventory')
    for name, checksum in files.items():
        if (not isinstance(name, str) or '\\' in name or ':' in name or name.startswith('/')
                or any(p in ('.', '..') for p in pathlib.PurePosixPath(name).parts)
                or str(pathlib.PurePosixPath(name)) != name):
            raise ValueError('Invalid inventory path')
        if configuration.source_digest(generated / name) != hw.hex_identity(checksum):
            raise ValueError('Staged inventory checksum mismatch: ' + name)
    return generated, manifest


def program(coordinator, include_sources=True):
    active = coordinator.active()
    coordinator.verify_active(active, active.get('build_hash'))
    if not active.get('build_hash'): raise ValueError('This active version has no saved source archive / debug metadata')
    version = coordinator.version(active['build_hash'])
    if not (version / 'stage-result.json').exists(): raise ValueError('This active version has no saved source archive / debug metadata')
    generated, manifest = inventory(version)
    compiled = hw.parse_json(hw.read_regular(version / 'compile-result.json'))
    if (manifest.get('ArtifactKind') == 'hardware-only' or compiled.get('state') != 'compiled' or compiled.get('exports_verified') is not True
            or hw.hex_identity(compiled.get('build_hash')) != hw.hex_identity(active['build_hash'])
            or hw.hex_identity(compiled.get('library_sha256')) != hw.hex_identity(active.get('library_sha256'))):
        raise ValueError('Active program has no verified successful compilation')
    debug = hw.read_regular(generated / 'debug-map.json')
    if hashlib.sha256(debug).hexdigest() != hw.hex_identity(manifest.get('SymbolTableHash')):
        raise ValueError('Debug map checksum differs')
    for active_key, manifest_key in [('source_hash','SourceHash'), ('hardware_hash','HardwareHash'), ('symbol_table_hash','SymbolTableHash')]:
        if active.get(active_key) and str(active[active_key]).lower() != str(manifest.get(manifest_key)).lower():
            raise ValueError('Active program identity differs: ' + active_key)
    transfer = dict(source_hash=manifest.get('SourceHash'), hardware_hash=manifest.get('HardwareHash'),
                    symbol_table_hash=manifest['SymbolTableHash'], debug_map=hw.parse_json(debug), debug_map_json=debug.decode('utf-8'))
    if include_sources:
        if 'engineering-archive.json' not in manifest['Files']:
            raise ValueError('该版本未保存源工程，无法还原 ST (source archive unavailable)')
        archive = hw.parse_json(read_archive(generated / 'engineering-archive.json'))
        if (archive.get('SchemaVersion') != 1 or archive.get('SourceHash') != manifest.get('SourceHash')
                or archive.get('HardwareHash') != manifest.get('HardwareHash')
                or not isinstance(archive.get('Project'), dict) or not isinstance(archive.get('Sources'), list)):
            raise ValueError('Source archive identity differs')
        transfer.update(project=archive['Project'], sources=archive['Sources'])
    if coordinator.active() != active: raise ValueError('Active program changed during export')
    if len(json.dumps(transfer).encode()) > MAX_TRANSFER: raise ValueError('Transfer exceeds capacity')
    return dict(state='program-exported' if include_sources else 'program-debug-map', active=active, transfer=transfer)


def hardware(coordinator):
    active = coordinator.active()
    record_path = pathlib.Path(coordinator.config.get('ethercat_active_record') or coordinator.prefix / 'hardware-active.json')
    if not record_path.exists(): raise ValueError('No verified applied hardware record is available')
    applied = hw.parse_json(hw.read_regular(record_path))
    version = coordinator.version(applied.get('buildHash'))
    generated, manifest = inventory(version)
    verified = hw.verify_applied(generated, coordinator.config)
    hw.matching_bundle(applied, verified)
    if 'hardware-configuration.json' not in manifest['Files']:
        raise ValueError('Applied hardware has no saved engineering configuration')
    document = hw.parse_json(hw.read_regular(generated / 'hardware-configuration.json'))
    if document.get('SchemaVersion') != 1 or document.get('State') != 'offline-engineering':
        raise ValueError('Unsupported saved hardware configuration')
    if 'axis-manifest.json' in manifest['Files']:
        raw = hw.read_regular(generated / 'axis-manifest.json')
        axes = validate_axes(axis_gate.strict_json(raw))
        loaded = (configuration.worker_digest(coordinator.config, axes['InterfaceVersion'])
                  if axes['InterfaceVersion'] == axis_gate.INTERFACE_V2
                  else configuration.worker_digest(coordinator.config)) if axes['Axes'] else None
        if axes['Axes'] and loaded != hashlib.sha256(raw).hexdigest():
            raise ValueError('Loaded axis manifest differs from applied hardware')
    if hw.parse_json(hw.read_regular(record_path)) != applied or coordinator.active() != active:
        raise ValueError('Applied hardware/program changed during export')
    return dict(state='hardware-exported', active=active, transfer=dict(configuration=document, applied=verified))
