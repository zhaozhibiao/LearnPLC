import asyncio
import importlib.util
import json
import io
import pathlib
import tempfile
import unittest
from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer

PATH = pathlib.Path(__file__).with_name('service.py')
spec = importlib.util.spec_from_file_location('engineering_service', PATH) if PATH.exists() else None
service = importlib.util.module_from_spec(spec) if spec else None
if spec:
    spec.loader.exec_module(service)


class ServiceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.assertIsNotNone(service, 'Engineering service implementation is required')
        self.calls = []
        self.failure = False
        self.mismatch = False
        async def coordinator(request):
            self.calls.append(request)
            if self.failure: raise ValueError('private-password')
            return dict(success=True, state='stopped', request_id='other' if self.mismatch else request['request_id'])
        self.backend_calls = []
        self.break_connection = False
        async def backend(request):
            self.backend_calls.append((request.method, request.path, request.headers.get('Authorization')))
            if self.break_connection:
                request.transport.close()
                return web.Response()
            if request.path == '/api/login':
                return web.json_response({'access_token': 'runtime.jwt.value'})
            if request.path == '/socket.io/':
                ws = web.WebSocketResponse(max_msg_size=1024)
                await ws.prepare(request)
                async for message in ws:
                    await ws.send_str(message.data)
                return ws
            return web.json_response({'ok': True})
        upstream = web.Application()
        upstream.router.add_route('*', '/{path:.*}', backend)
        self.upstream = TestServer(upstream)
        await self.upstream.start_server()
        self.client = TestClient(TestServer(service.create_app(
            pairing_token='p' * 43, coordinator=coordinator,
            backend_url=str(self.upstream.make_url('')).rstrip('/'), backend_ssl=None,
            credentials=lambda: {'username': 'private', 'password': 'private'})))
        await self.client.start_server()
        self.auth = {'Authorization': 'Bearer ' + 'p' * 43}
        self.proxy_auth = {'X-Engineering-Token': 'p' * 43, 'Authorization': 'Bearer runtime.jwt.value'}

    async def asyncTearDown(self):
        if hasattr(self, 'client'): await self.client.close()
        if hasattr(self, 'upstream'): await self.upstream.close()

    async def test_authentication_precedes_backend_or_coordinator(self):
        for path in ['/engineering/identity', '/api/status', '/socket.io/']:
            response = await self.client.get(path)
            self.assertEqual(401, response.status)
        self.assertEqual([], self.calls)
        self.assertEqual([], self.backend_calls)

    async def test_identity_is_coordinator_status(self):
        response = await self.client.get('/engineering/identity?request_id=read-1', headers=self.auth)
        self.assertEqual('read-1', (await response.json())['request_id'])
        self.assertEqual([{'action': 'status', 'request_id': 'read-1'}], self.calls)

    async def test_only_allowlisted_actions_and_fields_reach_coordinator(self):
        for request in [dict(action='shell', request_id='x'), dict(action='stage', request_id='x', package_path='/etc/passwd'), dict(action='status', request_id='../x')]:
            response = await self.client.post('/engineering/deploy', headers=self.auth, json=request)
            self.assertEqual(400, response.status)
        self.assertEqual([], self.calls)

    async def test_deploy_routes_once_and_matches_request(self):
        request = dict(action='program-export', request_id='export-1')
        response = await self.client.post('/engineering/deploy', headers=self.auth, json=request)
        self.assertEqual(200, response.status)
        self.assertEqual([request], self.calls)

    async def test_runtime_proxy_requires_pairing_and_preserves_jwt(self):
        response = await self.client.get('/api/status', headers={'Authorization': 'Bearer runtime.jwt.value'})
        self.assertEqual(401, response.status)
        response = await self.client.get('/api/status', headers=self.proxy_auth)
        self.assertEqual(200, response.status)
        self.assertEqual(('GET', '/api/status', 'Bearer runtime.jwt.value'), self.backend_calls[-1])

    async def test_private_session_is_bounded_and_returns_only_token(self):
        response = await self.client.post('/engineering/session', headers=self.auth)
        self.assertEqual({'access_token': 'runtime.jwt.value'}, await response.json())

    async def test_websocket_proxy_carries_engineio_messages(self):
        ws = await self.client.ws_connect('/socket.io/?EIO=4&transport=websocket', headers=self.proxy_auth)
        await ws.send_str('42["debug",{"x":1}]')
        self.assertEqual('42["debug",{"x":1}]', (await ws.receive(timeout=2)).data)
        await ws.close()

    async def test_oversize_deploy_is_rejected_before_coordinator(self):
        response = await self.client.post('/engineering/deploy', headers=self.auth, data=io.BytesIO(b'x' * (service.MAX_REQUEST + 1)))
        self.assertEqual(413, response.status)
        self.assertEqual([], self.calls)

    async def test_backend_down_does_not_break_engineering_health(self):
        await self.upstream.close()
        response = await self.client.get('/api/status', headers=self.proxy_auth)
        self.assertEqual(502, response.status)
        response = await self.client.get('/engineering/health', headers=self.auth)
        self.assertEqual({'success': True, 'service': 'engineering'}, await response.json())

    async def test_coordinator_failure_is_sanitized_without_retry(self):
        self.failure = True
        response = await self.client.post('/engineering/deploy', headers=self.auth, json=dict(action='activate', request_id='once'))
        self.assertEqual(502, response.status)
        self.assertNotIn('private-password', await response.text())
        self.assertEqual(1, len(self.calls))

    async def test_identity_mismatch_is_rejected(self):
        self.mismatch = True
        response = await self.client.get('/engineering/identity?request_id=read-1', headers=self.auth)
        self.assertEqual(502, response.status)

    async def test_configuration_identity_is_allowlisted(self):
        value = dict(action='status', request_id='bad', configuration_identity='1' * 64)
        self.assertEqual(value, service.validate_request(value))

    async def test_websocket_oversize_is_closed(self):
        ws = await self.client.ws_connect('/socket.io/?EIO=4&transport=websocket', headers=self.proxy_auth)
        await ws.send_str('x' * (service.MAX_WS + 1))
        response = await ws.receive(timeout=3)
        self.assertNotEqual('x' * (service.MAX_WS + 1), response.data)
        await ws.close()

    async def test_runtime_write_disconnect_does_not_retry(self):
        self.break_connection = True
        response = await self.client.post('/api/start', headers=self.proxy_auth, json={})
        self.assertEqual(502, response.status)
        self.assertEqual(1, len(self.backend_calls))


class ProductionPathTests(unittest.TestCase):
    def test_production_coordinator_path_is_installed_code_not_state(self):
        self.assertEqual(pathlib.Path('/home/a/st-workbench-deployment/coordinator.py'), service.COORDINATOR_PATH)


class SubprocessTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.old_coordinator_path = service.COORDINATOR_PATH
        self.old_root = service.ROOT
        service.ROOT = pathlib.Path(self.temporary.name)
        service.COORDINATOR_PATH = pathlib.Path(self.temporary.name) / 'coordinator.py'

    async def asyncTearDown(self):
        service.COORDINATOR_PATH = self.old_coordinator_path
        service.ROOT = self.old_root
        self.temporary.cleanup()

    def child(self, source):
        service.COORDINATOR_PATH.write_text(source, encoding='utf-8')

    async def test_real_stdin_protocol_and_sanitized_error(self):
        self.child('import sys,json\nr=json.load(sys.stdin)\nprint(json.dumps(dict(success=False,request_id=r["request_id"],message="secret")))')
        value = await service.coordinator(dict(action='status', request_id='actual-child'))
        self.assertEqual('actual-child', value['request_id'])
        self.assertNotIn('secret', value['message'])
        self.assertEqual('secret',json.loads((service.ROOT/'last-failure.json').read_text())['detail'])

    async def test_real_child_response_request_id_mismatch(self):
        self.child('print(\'{"success":true,"request_id":"wrong"}\')')
        with self.assertRaisesRegex(ValueError, 'mismatch'):
            await service.coordinator(dict(action='status', request_id='actual-child'))

    async def test_real_child_response_capacity_is_bounded(self):
        self.child('print("x" * 2048)')
        previous = service.MAX_RESPONSE
        service.MAX_RESPONSE = 1024
        try:
            with self.assertRaisesRegex(ValueError, 'capacity'):
                await service.coordinator(dict(action='status', request_id='actual-child'))
        finally: service.MAX_RESPONSE = previous

    async def test_real_child_timeout_is_bounded(self):
        self.child('import time\ntime.sleep(20)')
        previous = service.COORDINATOR_TIMEOUT
        service.COORDINATOR_TIMEOUT = 0.1
        try:
            with self.assertRaises(asyncio.TimeoutError):
                await service.coordinator(dict(action='status', request_id='actual-child'))
        finally: service.COORDINATOR_TIMEOUT = previous


class FailureMessages(unittest.TestCase):
    def test_service_preserves_host_namespace_for_root_broker_authentication(self):
        import configparser
        unit=configparser.ConfigParser()
        unit.read(PATH.with_name('stworkbench-engineering.service'))
        # User-service mount sandboxing implicitly creates a user namespace:
        # host root is unmapped there and SO_PEERCRED becomes overflow UID.
        self.assertEqual('no',unit['Service'].get('PrivateTmp'))
        self.assertEqual('no',unit['Service'].get('PrivateUsers'))
        self.assertEqual('yes',unit['Service'].get('NoNewPrivileges'))

    def test_hardware_stop_and_identity_rejections_are_actionable(self):
        self.assertIn('停止', service.failure_message('hardware-apply', 'Hardware apply requires PLC STOPPED; no implicit PLC stop is performed'))
        self.assertIn('活动程序', service.failure_message('hardware-apply', 'Active identity changed; refresh before retry'))
        self.assertIn('撤使能', service.failure_message('hardware-apply', 'Hardware broker rejected or unconfirmed: Every native axis must be Disabled and fault-free'))
        self.assertIn('停止结果', service.failure_message('stop', 'Runtime transition result unknown; query status'))

    def test_private_failure_record_is_bounded_and_does_not_record_request(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(service, 'ROOT', pathlib.Path(directory)):
                service.record_failure({'action':'hardware-apply','request_id':'failure-test','package_base64':'private-package','password':'private-password'}, 'private backend detail'+('x'*10000))
            saved=json.loads((pathlib.Path(directory)/'last-failure.json').read_text())
            self.assertEqual('hardware-apply',saved['action'])
            self.assertEqual('failure-test',saved['request_id'])
            self.assertLessEqual(len(saved['detail']),4096)
            self.assertNotIn('private-package',json.dumps(saved))
            self.assertNotIn('private-password',json.dumps(saved))

    def test_missing_motion_core_has_actionable_safe_message(self):
        message = service.failure_message('hardware-export','Export could not be verified: Native controller socket unavailable')
        self.assertIn('运动核心未启动', message)
        self.assertNotIn('/run/', message)

    def test_source_archive_absence_is_actionable_without_exposing_arbitrary_errors(self):
        self.assertIn('未保存源工程', service.failure_message('program-export','Export could not be verified: 该版本未保存源工程，无法还原 ST (source archive unavailable)'))
        self.assertNotIn('secret',service.failure_message('program-export','secret=not-for-output'))
        self.assertNotIn('secret',service.failure_message('start','secret=not-for-output'))

if __name__ == '__main__': unittest.main()
