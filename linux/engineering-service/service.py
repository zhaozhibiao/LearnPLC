"""User-owned HTTPS engineering gateway. No shell and no mutation retries."""
import asyncio
import contextlib
import hmac
import json
import os
import pathlib
import re
import signal
import ssl
import stat
import sys
import tempfile
import datetime
import uuid
from aiohttp import ClientSession, ClientTimeout, TCPConnector, WSMsgType, web

PREFIX = pathlib.Path('/home/a/.local/share/st-workbench/runtime-v4.3.0')
ROOT = PREFIX / 'engineering'
COORDINATOR_PATH = pathlib.Path('/home/a/st-workbench-deployment/coordinator.py')
MAX_REQUEST = 48 * 1024 * 1024
MAX_RESPONSE = 64 * 1024 * 1024
MAX_WS = 1024 * 1024
MAX_CONNECTIONS = 16
COORDINATOR_TIMEOUT = 600
ACTIONS = {'status', 'realtime-status', 'stage', 'compile', 'activate', 'recover',
           'start', 'stop', 'configuration-plan', 'configuration-apply', 'hardware-validate',
           'hardware-apply', 'hardware-stage', 'program-export', 'hardware-export', 'program-debug-map'}
FIELDS = {'action', 'request_id', 'build_hash', 'package_base64', 'package_sha256',
          'expected_active_build_hash', 'expected_hardware_hash', 'configuration_identity'}


def private_bytes(path, maximum=16384):
    descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_size > maximum:
            raise ValueError('Invalid private file')
        if os.name == 'posix' and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600):
            raise ValueError('Unsafe private file permissions')
        with os.fdopen(descriptor, 'rb', closefd=False) as stream:
            return stream.read(maximum + 1)
    finally:
        os.close(descriptor)


def credentials():
    value = json.loads(private_bytes(PREFIX / 'data/coordinator-auth.json'))
    if not isinstance(value, dict) or set(value) != {'username', 'password'} or not all(isinstance(x, str) and 0 < len(x) <= 4096 for x in value.values()):
        raise ValueError('Invalid private credentials')
    return value


def validate_request(value):
    if not isinstance(value, dict) or set(value) - FIELDS or value.get('action') not in ACTIONS:
        raise ValueError('Unsupported deployment request')
    if not isinstance(value.get('request_id'), str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}', value['request_id']):
        raise ValueError('Invalid request ID')
    for name in ('build_hash', 'package_sha256', 'expected_active_build_hash', 'expected_hardware_hash', 'configuration_identity'):
        if name in value and value[name] is not None and (not isinstance(value[name], str) or not re.fullmatch('[0-9a-fA-F]{64}', value[name])):
            raise ValueError('Invalid identity')
    if 'package_base64' in value and (value['action'] not in {'stage', 'hardware-stage'} or not isinstance(value['package_base64'], str) or len(value['package_base64']) > 32 * 1024 * 1024 * 4 // 3 + 4):
        raise ValueError('Invalid package')
    return value


async def bounded_read(stream, maximum):
    chunks = []
    size = 0
    while True:
        chunk = await stream.read(65536)
        if not chunk: break
        size += len(chunk)
        if size > maximum:
            raise ValueError('Response capacity exceeded')
        chunks.append(chunk)
    return b''.join(chunks)


def failure_message(action, message):
    if isinstance(message, str):
        if action in {'hardware-apply','configuration-apply'} and ('requires PLC STOPPED' in message or 'PLC must be STOPPED' in message):
            return '硬件应用前尚未确认 PLC 停止；请查询设备状态，确认停止后再下载。'
        if action in {'hardware-stage','hardware-validate','hardware-apply','configuration-apply','stop'}:
            if 'Active identity changed' in message or 'Active library bytes/path differ' in message:
                return '设备活动程序已变化或身份不一致；请刷新设备状态后重新确认操作。'
            if 'Every native axis must be Disabled and fault-free' in message:
                return '硬件尚未应用：请先停稳所有轴、撤使能并清除驱动故障，再下载硬件。'
            if 'Native outputs must be at configured safe state' in message:
                return '硬件尚未应用：IO 输出未到组态安全状态，请检查实际输出。'
            if 'Another deployment owns the instance' in message or 'Hardware apply is already running' in message:
                return '设备正在处理另一项下载操作，请查询实际状态后再操作。'
            if 'unfinished activation transaction' in message:
                return '上一次程序应用事务尚未结束；请先查询并恢复事务，再下载硬件。'
        if action == 'stop' and ('Runtime transition result unknown' in message or 'Runtime transition did not reach expected state' in message):
            return 'PLC 停止结果尚未确认；请查询设备状态，硬件未继续下发，不会自动重试停止。'
    if action == 'program-export' and isinstance(message,str) and ('该版本未保存源工程，无法还原 ST' in message or 'has no saved source archive' in message):
        return '该版本未保存源工程，无法还原 ST；使用新版下载程序后，该版本才支持完整上传。'
    if action in {'hardware-export', 'hardware-apply', 'configuration-plan', 'configuration-apply', 'start'} and isinstance(message, str):
        if 'Native controller socket unavailable' in message:
            return '运动核心未启动或控制通道不可用；请先恢复核心服务，或在 PLC 停止后下载硬件初始化。'
        if 'Native service state is unconfirmed' in message:
            return '运动核心服务状态无法确认；请检查服务状态，暂不重试硬件下载。'
        if 'Loaded native runtime is not ready' in message or 'Native runtime and IO must be ready' in message:
            return '运动核心或 IO 尚未就绪；请检查 EtherCAT 总线和核心服务。'
        if 'Loaded axis manifest differs' in message or 'Axis bridge capability is not ready' in message:
            return '轴通道未就绪或轴映射与实际组态不一致；请检查轴服务及已应用组态。'
    return 'Operation failed or unconfirmed; inspect actual state before retry.'


def record_failure(request, detail):
    """Owner-only diagnostic; never serialize credentials or package/request bodies."""
    action=request.get('action') if request.get('action') in ACTIONS else 'unknown'
    identifier=request.get('request_id','')
    if not isinstance(identifier,str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',identifier): identifier='unknown'
    value=dict(action=action,request_id=identifier,utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),detail=str(detail)[:4096])
    descriptor,temporary=tempfile.mkstemp(prefix='.failure-',dir=ROOT)
    try:
        with os.fdopen(descriptor,'w',encoding='utf-8') as stream:
            json.dump(value,stream,ensure_ascii=False)
        os.replace(temporary,ROOT/'last-failure.json')
    finally:
        if os.path.exists(temporary): os.unlink(temporary)


async def coordinator(request):
    process = await asyncio.create_subprocess_exec(
        sys.executable, str(COORDINATOR_PATH),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.DEVNULL, start_new_session=True)
    async def exchange():
        process.stdin.write(json.dumps(request, separators=(',', ':')).encode())
        await process.stdin.drain()
        process.stdin.close()
        raw = await bounded_read(process.stdout, MAX_RESPONSE)
        await process.wait()
        value = json.loads(raw)
        if not isinstance(value, dict) or value.get('request_id') != request['request_id']:
            raise ValueError('Coordinator response mismatch')
        if not value.get('success'):
            with contextlib.suppress(OSError): record_failure(request,value.get('message'))
            value['message'] = failure_message(request['action'],value.get('message'))
            labels={'hardware-stage':'硬件包暂存','hardware-validate':'硬件包校验','hardware-apply':'硬件应用','configuration-apply':'组态应用','stop':'停止 PLC'}
            if request['action'] in labels:
                value['message'] += '（步骤：'+labels[request['action']]+'；诊断号：'+request['request_id']+'）'
        return value
    try:
        return await asyncio.wait_for(exchange(), COORDINATOR_TIMEOUT)
    finally:
        if process.returncode is None:
            if os.name == 'posix':
                with contextlib.suppress(ProcessLookupError): os.killpg(process.pid, signal.SIGKILL)
            else: process.kill()
            await process.wait()


def create_app(pairing_token, coordinator=coordinator, backend_url='https://localhost:8443', backend_ssl=None, credentials=credentials):
    if not re.fullmatch(r'[A-Za-z0-9_-]{43,128}', pairing_token):
        raise ValueError('Invalid pairing token')
    active = set()
    ws_count = 0
    in_flight = 0
    @web.middleware
    async def guard(request, handler):
        nonlocal in_flight
        token = request.headers.get('X-Engineering-Token', '')
        if not token and request.path.startswith('/engineering/'):
            auth = request.headers.get('Authorization', '')
            token = auth[7:] if auth.startswith('Bearer ') else ''
        if not hmac.compare_digest(token.encode(), pairing_token.encode()):
            return web.json_response({'error': 'Unauthorized'}, status=401)
        if in_flight >= MAX_CONNECTIONS:
            return web.json_response({'error': 'Connection limit'}, status=429)
        in_flight += 1
        try:
            return await asyncio.wait_for(handler(request), COORDINATOR_TIMEOUT + 10 if request.path == '/engineering/deploy' else 3600 if request.path == '/socket.io/' else 30)
        except web.HTTPException:
            raise
        except Exception:
            return web.json_response({'error': 'Backend unavailable or operation unconfirmed; no retry performed.'}, status=502)
        finally:
            in_flight -= 1
    app = web.Application(client_max_size=MAX_REQUEST, middlewares=[guard])
    app['coordinator'] = coordinator

    async def invoke(request, value):
        validate_request(value)
        identifier = value['request_id']
        if identifier in active:
            return web.json_response({'error': 'Request already in flight'}, status=409)
        active.add(identifier)
        try:
            result = await app['coordinator'](value)
            if result.get('request_id') != identifier: raise ValueError('Response mismatch')
            return web.json_response(result)
        finally:
            active.remove(identifier)

    async def deploy(request):
        try:
            value = json.loads(await request.read())
            validate_request(value)
        except (ValueError, TypeError):
            return web.json_response({'error': 'Invalid deployment request'}, status=400)
        return await invoke(request, value)

    async def identity(request):
        value = {'action': 'status', 'request_id': request.query.get('request_id', uuid.uuid4().hex)}
        try: validate_request(value)
        except ValueError: return web.json_response({'error': 'Invalid request ID'}, status=400)
        return await invoke(request, value)

    def session():
        # Fresh connection for each exchange prevents persistent-connection retry behavior.
        return ClientSession(connector=TCPConnector(force_close=True, limit=MAX_CONNECTIONS),
                             timeout=ClientTimeout(total=25), auto_decompress=False, trust_env=False)

    async def login(request):
        async with session() as client:
            async with client.post(backend_url + '/api/login', json=credentials(), ssl=backend_ssl, allow_redirects=False) as response:
                if response.status != 200: raise ValueError('Runtime login failed')
                value = json.loads(await bounded_read(response.content, 16384))
                token = value.get('access_token')
                if not isinstance(token, str) or not re.fullmatch(r'[A-Za-z0-9._~+/=-]{1,12000}', token):
                    raise ValueError('Invalid Runtime session')
                return web.json_response({'access_token': token}, headers={'Cache-Control': 'no-store'})

    async def proxy(request):
        nonlocal ws_count
        # Never forward pairing material, cookies, client Host, or proxy headers.
        headers = {name: request.headers[name] for name in ('Authorization', 'Content-Type', 'Accept') if name in request.headers}
        target = backend_url + request.raw_path
        if request.path == '/socket.io/' and request.headers.get('Upgrade', '').lower() == 'websocket':
            if ws_count >= 8: return web.json_response({'error': 'Socket limit'}, status=429)
            ws_count += 1
            try:
                async with session() as client:
                    async with client.ws_connect(target, headers=headers, ssl=backend_ssl, max_msg_size=MAX_WS, heartbeat=20) as upstream:
                        downstream = web.WebSocketResponse(max_msg_size=MAX_WS, heartbeat=20)
                        await downstream.prepare(request)
                        async def pump(source, destination):
                            async for message in source:
                                if message.type == WSMsgType.TEXT: await destination.send_str(message.data)
                                elif message.type == WSMsgType.BINARY: await destination.send_bytes(message.data)
                                else: break
                            await destination.close()
                        tasks = [asyncio.create_task(pump(upstream, downstream)), asyncio.create_task(pump(downstream, upstream))]
                        try:
                            await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                        finally:
                            for task in tasks: task.cancel()
                            await asyncio.gather(*tasks, return_exceptions=True)
                            await downstream.close()
                        return downstream
            finally: ws_count -= 1
        body = await request.read()
        async with session() as client:
            async with client.request(request.method, target, data=body, headers=headers, ssl=backend_ssl, allow_redirects=False) as response:
                raw = await bounded_read(response.content, MAX_RESPONSE)
                if response.status >= 500:
                    return web.json_response({'error': 'Runtime unavailable'}, status=502)
                returned = {name: response.headers[name] for name in ('Content-Type', 'Content-Encoding') if name in response.headers}
                return web.Response(body=raw, status=response.status, headers=returned)

    async def health(request): return web.json_response({'success': True, 'service': 'engineering'})
    app.router.add_get('/engineering/health', health)
    app.router.add_get('/engineering/identity', identity)
    app.router.add_post('/engineering/deploy', deploy)
    app.router.add_post('/engineering/session', login)
    app.router.add_route('*', '/api/{path:.*}', proxy)
    app.router.add_route('*', '/socket.io/', proxy)
    return app


def main():
    os.umask(0o077)
    if pathlib.Path.home() != pathlib.Path('/home/a') or os.getuid() == 0:
        raise ValueError('Unexpected engineering service user')
    token = private_bytes(ROOT / 'pairing-token').decode().strip()
    backend_ssl = ssl.create_default_context(cafile=PREFIX / 'source/webserver/certOPENPLC.pem')
    server_ssl = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_ssl.minimum_version = ssl.TLSVersion.TLSv1_2
    private_bytes(ROOT / 'key.pem')
    server_ssl.load_cert_chain(ROOT / 'cert.pem', ROOT / 'key.pem')
    app = create_app(token, backend_ssl=backend_ssl)
    web.run_app(app, host='0.0.0.0', port=18445, ssl_context=server_ssl, access_log=None, print=None)


if __name__ == '__main__': main()
