"""Native-only backend boundaries. No models, private data or deployed services."""
import http.client
import threading
import unittest
from unittest.mock import patch

from aiohttp import ClientSession, WSServerHandshakeError, web
from aiohttp.test_utils import TestServer

import local_parakeet
import server


class GatewayOrigin(unittest.IsolatedAsyncioTestCase):
    async def test_browser_websocket_rejected_before_handler(self):
        called = []

        async def handler(request):
            called.append(True)
            return web.Response(text='native request accepted')

        app = web.Application(middlewares=[server.authenticate])
        app['config'] = {'api_key': 'test-key'}
        app.router.add_get('/ws', handler)
        async with TestServer(app) as test, ClientSession() as client:
            for origin in ('https://untrusted.example', 'http://127.0.0.1', 'null', ''):
                with self.subTest(origin=origin):
                    with self.assertRaises(WSServerHandshakeError) as error:
                        await client.ws_connect(test.make_url('/ws'), headers={
                            'Origin': origin, 'Authorization': 'Bearer test-key'})
                    self.assertEqual(error.exception.status, 403)
            self.assertEqual(called, [])
            async with client.get(test.make_url('/ws')) as response:
                self.assertEqual(response.status, 401)
            async with client.get(test.make_url('/ws'), headers={
                    'Authorization': 'Bearer test-key'}) as response:
                self.assertEqual(response.status, 200)
            self.assertEqual(called, [True])
            # Loopback deployments default to no token: the origin guard must
            # still reject a web page before it can occupy the single session.
            app['config']['api_key'] = ''
            with self.assertRaises(WSServerHandshakeError) as error:
                await client.ws_connect(test.make_url('/ws'), headers={
                    'Origin': 'https://untrusted.example'})
            self.assertEqual(error.exception.status, 403)
            self.assertEqual(called, [True])


class ParakeetOrigin(unittest.TestCase):
    def setUp(self):
        self.server = local_parakeet.ThreadingHTTPServer(
            ('127.0.0.1', 0), local_parakeet.Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.old_warm = local_parakeet.keep_warm
        local_parakeet.keep_warm = False

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        local_parakeet.keep_warm = self.old_warm

    def post(self, path, content_type, origin=None):
        conn = http.client.HTTPConnection(*self.server.server_address, timeout=2)
        headers = {'Content-Type': content_type}
        if origin is not None:
            headers['Origin'] = origin
        try:
            conn.request('POST', path, b'{"keep_warm":true}', headers)
            response = conn.getresponse()
            response.read()
            return response.status
        finally:
            conn.close()

    @patch('local_parakeet.load')
    def test_browser_posts_cannot_load_model(self, loader):
        for origin in ('https://untrusted.example', 'null', ''):
            for path, content_type in (('/warm', 'text/plain'),
                                       ('/warm', 'application/json'),
                                       ('/v1/audio/transcriptions', 'multipart/form-data')):
                with self.subTest(origin=origin, path=path):
                    self.assertEqual(self.post(path, content_type, origin), 403)
        self.assertFalse(local_parakeet.keep_warm)
        loader.assert_not_called()

    @patch('local_parakeet.load')
    def test_native_content_types_and_warm_request(self, loader):
        self.assertEqual(self.post('/warm', 'text/plain'), 415)
        self.assertEqual(self.post('/v1/audio/transcriptions', 'text/plain'), 415)
        self.assertEqual(self.post('/unknown', 'application/json'), 404)
        loader.assert_not_called()
        self.assertEqual(self.post('/warm', 'application/json; charset=utf-8'), 200)
        self.assertTrue(local_parakeet.keep_warm)
        loader.assert_called_once()


if __name__ == '__main__':
    unittest.main()
