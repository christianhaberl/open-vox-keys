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
    async def setup_gateway(self, batch_ok=True, stream_ok=True, batch_text="Batch complete.", stream_text="Stream complete.", early_stream=False, config=None, batch_delay=0):
        self.batch_calls = 0
        async def batch(r):
            await r.read()
            self.batch_calls += 1
            await asyncio.sleep(batch_delay)
            if not batch_ok:
                raise web.HTTPServiceUnavailable()
            return web.json_response(dict(text=batch_text[self.batch_calls-1] if isinstance(batch_text, list) else batch_text))
        async def stream(r):
            if early_stream:
                await r.content.read(4)
            else:
                await r.read()
            if not stream_ok:
                raise web.HTTPServiceUnavailable()
            messages = [dict(type='transcript.text.delta', delta=stream_text),
                        dict(type='transcript.text.done', text=stream_text)]
            return web.Response(text=''.join('data: '+json.dumps(m)+'\n\n' for m in messages)+'data: [DONE]\n\n', content_type='text/event-stream')
        backend = web.Application()
        backend.router.add_post('/inference', batch)
        backend.router.add_post('/v1/audio/transcriptions/live', stream)
        self.backend = TestServer(backend)
        await self.backend.start_server()
        cfg = dict(vad_model='unused', batch_url=str(self.backend.make_url('/inference')),
                   stream_url=str(self.backend.make_url('/v1/audio/transcriptions/live')))
        cfg.update(config or {})
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

    async def test_voxtral_only_never_calls_batch(self):
        await self.setup_gateway(config={'batch_enabled': False})
        finals = [e for e in await self.run_dictation() if e['type'] == 'result']
        self.assertEqual(len(finals), 1)
        self.assertEqual(finals[0]['source'], 'voxtral')
        self.assertEqual(finals[0]['text'], 'Stream complete.')
        self.assertEqual(finals[0]['samples'], 32000)
        self.assertEqual(self.batch_calls, 0)

    async def test_voxtral_only_failure_does_not_use_batch(self):
        await self.setup_gateway(config={'batch_enabled': False}, stream_ok=False)
        events = await self.run_dictation()
        self.assertTrue(any(e['type'] == 'error' for e in events))
        self.assertFalse(any(e['type'] == 'result' for e in events))
        self.assertEqual(self.batch_calls, 0)

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

    async def test_empty_batch_uses_complete_stream(self):
        await self.setup_gateway(batch_text='  ')
        finals=[e for e in await self.run_dictation() if e['type']=='result']
        self.assertEqual(finals[0]['source'],'voxtral-fallback')
        self.assertEqual(finals[0]['text'],'Stream complete.')

    async def test_both_empty_with_speech_is_error(self):
        await self.setup_gateway(batch_text='',stream_text='')
        events=await self.run_dictation()
        self.assertFalse(any(e['type']=='result' for e in events))
        self.assertTrue(any(e['type']=='error' for e in events))

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

    async def test_early_stream_final_after_last_audio_is_not_complete(self):
        await self.setup_gateway(batch_ok=False, early_stream=True, stream_text='Only a prefix.')
        events = []
        with patch.object(server.Silero, 'frame', return_value=.9):
            async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
                await ws.send_json(dict(type='start'))
                await ws.receive_json()
                await ws.send_bytes(b'\1\0' * 32000)
                # Wait for the early streaming result before sending stop. There is
                # intentionally no subsequent audio packet to invalidate it.
                while True:
                    event = await ws.receive_json(timeout=2)
                    events.append(event)
                    if event['type'] == 'warning':
                        break
                await ws.send_str('stop')
                async for msg in ws:
                    events.append(json.loads(msg.data))
        self.assertFalse(any(e['type'] == 'result' for e in events))
        self.assertTrue(any(e['type'] == 'error' for e in events))

    async def test_vad_false_negative_cannot_drop_nonzero_section(self):
        await self.setup_gateway(batch_text=['First sentence.', 'Second sentence.'],
                                 stream_text='First sentence. Second sentence.')
        events = []
        probabilities = iter([.9] * 94 + [.1] * 94)
        with patch.object(server.Silero, 'frame', side_effect=lambda _: next(probabilities)):
            async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
                await ws.send_json(dict(type='start', segmentation=dict(
                    maximum=3, wait=0, signals=['vad', 'length'])))
                await ws.receive_json()
                for _ in range(6):
                    await ws.send_bytes(b'\1\0' * 16000)
                await ws.send_str('stop')
                async for msg in ws:
                    events.append(json.loads(msg.data))
        final = next(e for e in events if e['type'] == 'result')
        self.assertEqual(final['text'], 'First sentence. Second sentence.')
        self.assertEqual(final['samples'], 96000)
        self.assertEqual(self.batch_calls, 2)

    async def test_final_batch_deadline_uses_complete_stream(self):
        await self.setup_gateway(batch_delay=.2)
        with patch.object(server.Silero, 'frame', return_value=.9):
            async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
                await ws.send_json(dict(type='start', finish_timeout_s=.02))
                await ws.receive_json()
                await ws.send_bytes(b'\1\0' * 32000)
                await ws.send_str('stop')
                events = [json.loads(msg.data) async for msg in ws]
        final = next(e for e in events if e['type'] == 'result')
        self.assertEqual(final['source'], 'voxtral-fallback')
        self.assertEqual(final['text'], 'Stream complete.')

    async def test_digital_silence_does_not_call_batch(self):
        await self.setup_gateway(stream_text='')
        with patch.object(server.Silero, 'frame', return_value=.1):
            async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
                await ws.send_json(dict(type='start'))
                await ws.receive_json()
                await ws.send_bytes(b'\0\0' * 32000)
                await ws.send_str('stop')
                events = [json.loads(msg.data) async for msg in ws]
        self.assertEqual(self.batch_calls, 0)
        self.assertEqual(next(e for e in events if e['type'] == 'result')['text'], '')

    async def test_idle_capture_releases_exclusive_slot(self):
        await self.setup_gateway(config=dict(capture_idle_seconds=.05))
        async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
            await ws.send_json(dict(type='start'))
            await ws.receive_json()
            events = [json.loads(msg.data) async for msg in ws]
        self.assertTrue(any(e['type'] == 'error' and 'timed out' in e['text'] for e in events))
        self.assertFalse(self.gateway.app['busy'])

    async def test_active_capture_still_has_wall_clock_limit(self):
        await self.setup_gateway(config=dict(capture_idle_seconds=1, capture_max_seconds=.1))
        async with self.http.ws_connect(self.gateway.make_url('/ws')) as ws:
            await ws.send_json(dict(type='start'))
            await ws.receive_json()
            async def send_audio():
                try:
                    while not ws.closed:
                        await ws.send_bytes(b'\0\0' * 512)
                        await asyncio.sleep(.01)
                except Exception:
                    pass
            sender = asyncio.create_task(send_audio())
            with patch.object(server.Silero, 'frame', return_value=.1):
                events = [json.loads(msg.data) async for msg in ws]
            sender.cancel()
            await asyncio.gather(sender, return_exceptions=True)
        self.assertTrue(any(e['type'] == 'error' for e in events))
        self.assertFalse(self.gateway.app['busy'])

if __name__=='__main__':
    unittest.main()
