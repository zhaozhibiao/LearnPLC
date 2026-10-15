import pathlib
import re

def apply(root, runtime_dir):
    if not re.fullmatch(r'/[A-Za-z0-9_./-]+', runtime_dir) or '..' in runtime_dir.split('/'):
        raise ValueError('A simple absolute private runtime directory is required')
    if len((runtime_dir + '/plc_runtime.socket').encode()) >= 104:
        raise ValueError('Private UNIX socket path is too long')
    replacements = {
        'webserver/app.py': [
            ('etherdog_manager = EtherDogManager()', 'etherdog_manager = EtherDogManager(binary="./build/etherdog-disabled", run_dir=Path(' + repr(runtime_dir) + '))', 1),
            ('etherdog_manager.start()', '# Hardware master disabled for this independent ST Runtime.', 1),
            ('runtime_path="./build/plc_main"', 'runtime_path=str(Path(__file__).resolve().parents[1] / "build/plc_main")', 1),
            ('/run/runtime/', runtime_dir + '/', 2),
            ('network_discovery_responder.start()', '# LAN discovery disabled; use the explicit SSH tunnel.', 1),
            ('network_discovery_responder.stop()', '# LAN discovery was not started.', 1),
            ('host="0.0.0.0"', 'host="127.0.0.1"', 1),
            ('port=8443', 'port=8443', 1),
        ],
        'core/src/plc_app/unix_socket.h': [('/run/runtime/', runtime_dir + '/', 1)],
        'core/src/plc_app/utils/log.h': [('/run/runtime/', runtime_dir + '/', 1)],
        'core/src/plc_app/task_policy.h': [('/run/runtime/', runtime_dir + '/', 1)],
        'webserver/runtimemanager.py': [('cmd = [self.runtime_path]', 'cmd = [self.runtime_path, "--safe-mode"]', 2)],
    }
    pending = []
    for name, edits in replacements.items():
        target = root / name
        text = target.read_text(encoding='utf-8')
        for before, after, count in edits:
            if before != after and text.count(before) == 0 and text.count(after) == count:
                continue
            if text.count(before) != count:
                raise ValueError('Unexpected upstream content in ' + name)
            text = text.replace(before, after)
        if name.endswith('.py'):
            compile(text, name, 'exec')
        pending.append((target, text))
    # Validate every replacement before any write; never operate on installed shared services.
    for target, text in pending:
        target.write_text(text, encoding='utf-8', newline='\n')

if __name__ == '__main__':
    import sys
    apply(pathlib.Path(sys.argv[1]), sys.argv[2])
