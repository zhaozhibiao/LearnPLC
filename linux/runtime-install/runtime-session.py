"""Private SSH session bridge. Stdout is secret JSON for its owning process only."""
import json
import os
import pathlib
import ssl
import stat
import urllib.request

def read_private_credentials(path):
    try: descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
    except OSError: raise ValueError('Invalid private credential storage') from None
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_size > 16384:
            raise ValueError('Invalid private credential storage')
        if os.name == 'posix' and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600):
            raise ValueError('Unsafe private credential permissions')
        with os.fdopen(descriptor, 'r', encoding='utf-8') as stream:
            descriptor = -1
            value = json.load(stream)
        if not isinstance(value, dict) or set(value) != {'username', 'password'} or not all(isinstance(v, str) and v and len(v) <= 4096 for v in value.values()):
            raise ValueError('Invalid private credential schema')
        return value
    finally:
        if descriptor >= 0: os.close(descriptor)

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None

def main():
    prefix = pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0')
    if pathlib.Path.home() != pathlib.Path('/home/a'): raise ValueError('Unexpected session user')
    auth = read_private_credentials(prefix / 'data/coordinator-auth.json')
    context = ssl.create_default_context(cafile=prefix / 'source/webserver/certOPENPLC.pem')
    opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), NoRedirect())
    request = urllib.request.Request('https://localhost:8443/api/login', data=json.dumps(auth).encode(), headers={'Content-Type': 'application/json'})
    with opener.open(request, timeout=10) as response:
        if response.status != 200: raise ValueError('Session authentication failed')
        body = response.read(16385)
    if len(body) > 16384: raise ValueError('Session response exceeds limit')
    token = json.loads(body).get('access_token')
    if not isinstance(token, str) or not token or len(token) > 12000 or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~+/=' for c in token):
        raise ValueError('Invalid session token')
    print(json.dumps({'access_token': token}))

if __name__ == '__main__':
    try: main()
    except Exception:
        print('{"error":"Private session authentication unavailable"}')
        raise SystemExit(1)
