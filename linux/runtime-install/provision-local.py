"""Bootstrap only the private loopback Runtime; never print passwords or bearer tokens."""
import json
import os
import pathlib
import secrets
import ssl
import urllib.error
import urllib.request

def credentials(path):
    path=pathlib.Path(path)
    if path.is_symlink(): raise ValueError('Private auth file may not be a symbolic link')
    if path.exists():
        if os.name=='posix' and (path.stat().st_uid!=os.getuid() or path.stat().st_mode & 0o077):
            raise ValueError('Private auth file permissions are unsafe')
        try: value=json.loads(path.read_text(encoding='utf-8'))
        except (ValueError,UnicodeError): raise ValueError('Private auth file is invalid') from None
        if not isinstance(value,dict) or set(value)!={'username','password'} or not all(isinstance(v,str) and v for v in value.values()):
            raise ValueError('Private auth file schema is invalid')
        return value
    value={'username':'stworkbench-coordinator','password':secrets.token_urlsafe(32)}
    descriptor=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
    with os.fdopen(descriptor,'w',encoding='utf-8') as stream:
        json.dump(value,stream); stream.flush(); os.fsync(stream.fileno())
    return value

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs): return None

def main():
    prefix=pathlib.Path.home()/'.local/share/st-workbench/runtime-v4.3.0'
    if str(prefix.resolve())!='/home/a/.local/share/st-workbench/runtime-v4.3.0': raise ValueError('Unexpected private prefix')
    context=ssl.create_default_context(cafile=prefix/'source/webserver/certOPENPLC.pem')
    opener=urllib.request.build_opener(urllib.request.HTTPSHandler(context=context),NoRedirect())
    def request(endpoint,body=None,token=None):
        headers={'Content-Type':'application/json'}
        if token: headers['Authorization']='Bearer '+token
        operation=urllib.request.Request('https://localhost:8443/api/'+endpoint,data=None if body is None else json.dumps(body).encode(),headers=headers)
        try: response=opener.open(operation,timeout=10)
        except urllib.error.HTTPError as error: response=error
        with response:
            payload=response.read(2*1024*1024+1)
            if len(payload)>2*1024*1024: raise ValueError('Runtime response size limit exceeded')
            return response.code,json.loads(payload)
    status,users=request('get-users-info')
    if status not in (200,404): raise ValueError('Account presence check failed')
    auth=credentials(prefix/'data/coordinator-auth.json')
    if status==404:
        if users.get('msg')!='No users found': raise ValueError('Unexpected bootstrap response')
        status,created=request('create-user',auth)
        if status!=201 or created.get('role')!='admin': raise ValueError('Account bootstrap failed')
        print('Private first administrator initialized.')
    status,login=request('login',auth)
    if status!=200 or not isinstance(login.get('access_token'),str): raise ValueError('Private Runtime login failed')
    token=login['access_token']
    for endpoint in ('status','compilation-status'):
        status,payload=request(endpoint,token=token)
        if status!=200: raise ValueError('Authenticated read failed')
        print(endpoint+': '+json.dumps({'status':payload.get('status'),'exit_code':payload.get('exit_code')}))
    print('Credentials retained privately with mode0600; tokens remain in memory.')

if __name__=='__main__':
    try: main()
    except Exception:
        print('Private Runtime provisioning/read verification failed; inspect private installation state without printing credentials.')
        raise SystemExit(1)
