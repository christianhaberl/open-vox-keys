"""Fault-path tests: completed transcript selection, no partial fallback, cleanup."""
import asyncio
import json
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from aiohttp import web, ClientSession
from aiohttp.test_utils import TestServer
import server


class FakeVad:
    def get_inputs(self):
        return [SimpleNamespace(name=n) for n in ('input', 'h', 'c')]


class Gateway(unittest.IsolatedAsyncioTestCase):
    async def setup_gateway(self, batch_ok=True, stream_ok=True):
        async def batch(r):
            await r.read()
            if not batch_ok:
                raise web.HTTPServiceUnavailable()
            return web.json_response(dict(text='Batch complete.'))
        async def stream(r):
            await r.read()
            if not stream_ok:
                raise web.HTTPServiceUnavailable()
            messages = [dict(type='transcript.text.delta', delta='Stream complete.'),
                        dict(type='transcript.text.done', text='Stream complete.')]
            return web.Response(text=''.join('data: '+json.dumps(m)+'\n\n' for m in messages)+'data: [DONE]\n\n', content_type='text/event-stream')
        backend = web.Application()
        backend.router.add_post('/inference', batch)
        backend.router.add_post('/v1/audio/transcriptions/live', stream)
        self.backend = TestServer(backend)
        await self.backend.start_server()
        cfg = dict(vad_model='unused', batch_url=str(self.backend.make_url('/inference')),
                   stream_url=str(self.backend.make_url('/v1/audio/transcriptions/live')))
        with patch('server.ort.InferenceSession', return_value=FakeVad()):
            app = server.create_app(cfg)
        self.gateway = TestServer(app)
        await self.gateway.start_server()
        self.http = ClientSession()
        self.addAsyncCleanup(self.http.close)
        self.addAsyncCleanup(self.gateway.close)
        self.addAsyncCleanup(self.backend.close)

    async def run_dictation(self):
        out=[]
        with patch.object(server.Silero, 'frame', return_value=.9):
            async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
                await ws.send_json(dict(type='start'))
                self.assertEqual((await ws.receive_json())['type'], 'ready')
                await ws.send_bytes(b'\1\0'*32000)
                await ws.send_str('stop')
                async for msg in ws:
                    out.append(json.loads(msg.data))
        return out

    async def test_primary_success(self):
        await self.setup_gateway()
        events=await self.run_dictation()
        finals=[e for e in events if e['type']=='result']
        self.assertEqual(len(finals),1)
        self.assertEqual(finals[0]['text'],'Batch complete.')
        self.assertEqual(finals[0]['samples'],32000)
        self.assertEqual(finals[0]['source'],'whisper')
        self.assertFalse(self.gateway.app['busy'])

    async def test_batch_failure_uses_complete_stream_once(self):
        await self.setup_gateway(batch_ok=False)
        finals=[e for e in await self.run_dictation() if e['type']=='result']
        self.assertEqual(len(finals),1)
        self.assertEqual(finals[0]['text'],'Stream complete.')
        self.assertEqual(finals[0]['source'],'voxtral-fallback')

    async def test_stream_failure_keeps_batch(self):
        await self.setup_gateway(stream_ok=False)
        finals=[e for e in await self.run_dictation() if e['type']=='result']
        self.assertEqual(finals[0]['source'],'whisper')

    async def test_both_failed_never_return_partial_text(self):
        await self.setup_gateway(batch_ok=False,stream_ok=False)
        events=await self.run_dictation()
        self.assertFalse(any(e['type']=='result' for e in events))
        self.assertTrue(any(e['type']=='error' for e in events))
        self.assertFalse(self.gateway.app['busy'])

    async def test_disconnect_releases_busy(self):
        await self.setup_gateway()
        async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
            await ws.send_json(dict(type='start'))
            await ws.receive_json()
        for _ in range(30):
            if not self.gateway.app['busy']:
                break
            await asyncio.sleep(.01)
        self.assertFalse(self.gateway.app['busy'])

if __name__=='__main__':
    unittest.main()
