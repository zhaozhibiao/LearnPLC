"""Secret stdout JSON. Invoke only over authenticated SSH into a protected consumer."""
import hashlib
import importlib.util
import ipaddress
import json
import pathlib
import ssl
import sys


def pairing(root, address):
    ipaddress.ip_address(address)
    spec = importlib.util.spec_from_file_location('engineering_service', root / 'service.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    token = module.private_bytes(root / 'pairing-token').decode().strip()
    pem = (root / 'cert.pem').read_text(encoding='ascii')
    fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()
    return {'endpoint': 'https://' + address + ':18445', 'token': token, 'certificate_sha256': fingerprint}


if __name__ == '__main__':
    if pathlib.Path.home() != pathlib.Path('/home/a'):
        raise SystemExit('Unexpected pairing user')
    print(json.dumps(pairing(pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0/engineering'), sys.argv[1] if len(sys.argv) == 2 else '192.168.1.31')))
