"""Bootstrap pip into a private existing --without-pip venv, without root."""
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request

MAX_METADATA = 4 * 1024 * 1024
MAX_WHEEL = 16 * 1024 * 1024
METADATA_URL = 'https://pypi.org/pypi/pip/json'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None


def official_url(url):
    value = urllib.parse.urlsplit(url)
    if value.scheme != 'https' or value.hostname not in {'pypi.org', 'files.pythonhosted.org'} or value.username or value.password or value.port not in (None, 443):
        raise ValueError('Only official HTTPS package hosts are allowed')
    return value


def download(url):
    official_url(url)
    maximum = MAX_METADATA if url == METADATA_URL else MAX_WHEEL
    opener = urllib.request.build_opener(NoRedirect())
    with opener.open(url, timeout=30) as response:
        if response.geturl() != url: raise ValueError('Redirect refused')
        data = response.read(maximum + 1)
    if len(data) > maximum: raise ValueError('Package download capacity exceeded')
    return data


def fetch_wheel(directory, fetch=download):
    metadata = json.loads(fetch(METADATA_URL))
    version = metadata['info']['version']
    if not isinstance(version, str) or re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}', version) is None:
        raise ValueError('Only a stable pip release is supported')
    choices = [entry for entry in metadata['urls'] if entry.get('packagetype') == 'bdist_wheel'
               and entry.get('filename') == 'pip-' + version + '-py3-none-any.whl']
    if len(choices) != 1: raise ValueError('Expected one reviewed universal pip wheel')
    entry = choices[0]
    if official_url(entry['url']).hostname != 'files.pythonhosted.org':
        raise ValueError('Wheel must come from official package host')
    expected = entry['digests']['sha256']
    if not isinstance(expected, str) or not re.fullmatch('[0-9a-f]{64}', expected):
        raise ValueError('Invalid wheel checksum')
    data = fetch(entry['url'])
    if len(data) > MAX_WHEEL or hashlib.sha256(data).hexdigest() != expected:
        raise ValueError('Wheel checksum or capacity mismatch')
    private_directory = pathlib.Path(tempfile.mkdtemp(prefix='pip-bootstrap-', dir=directory))
    path = private_directory / entry['filename']
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(descriptor, 'wb') as output: output.write(data)
    return path, version


def main(venv):
    os.umask(0o077)
    venv = pathlib.Path(venv).resolve(strict=True)
    executable = venv / 'bin/python'
    if not (venv / 'pyvenv.cfg').is_file() or not executable.is_file():
        raise ValueError('An existing private Linux virtual environment is required')
    wheel, version = fetch_wheel(venv)
    environment = dict(os.environ, PYTHONPATH=str(wheel), PIP_CONFIG_FILE='/dev/null',
                       PIP_INDEX_URL='https://pypi.org/simple', PIP_DISABLE_PIP_VERSION_CHECK='1')
    subprocess.run([str(executable), '-m', 'pip', 'install', '--no-deps', '--only-binary=:all:',
                    '--no-index', '--find-links', str(wheel.parent), 'pip==' + version],
                   env=environment, check=True, timeout=120, stdout=sys.stderr)
    record = venv / 'pip-bootstrap.json'
    descriptor = os.open(record, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(descriptor, 'w') as output:
        json.dump({'name': 'pip', 'version': version, 'license': 'MIT',
                   'metadata_url': METADATA_URL, 'wheel_sha256': hashlib.sha256(wheel.read_bytes()).hexdigest()}, output)


if __name__ == '__main__':
    if len(sys.argv) != 2: raise SystemExit('Usage: bootstrap-pip.py EXISTING_PRIVATE_VENV')
    main(sys.argv[1])
