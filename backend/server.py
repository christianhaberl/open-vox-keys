"""Streaming dictation gateway. Audio exists only in session memory.

Voxtral supplies punctuation/live preview; Silero supplies acoustic cuts.
Whisper processes contiguous sections while capture continues. A complete
Voxtral transcript can replace a failed batch run; never guess word alignment.
"""
import argparse
import asyncio
import contextlib
import io
import json
import os
import wave

import aiohttp
from aiohttp import web
import numpy as np
import onnxruntime as ort
from segments import Options, Segmenter, SR


class Silero:
    def __init__(self, session):
        self.session = session
        self.h = np.zeros((1, 1, 128), np.float32)
        self.c = self.h.copy()
        self.context = np.zeros(64, np.float32)

    def frame(self, pcm):
        x = np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768
        p, self.h, self.c = self.session.run(None, {
            'input': np.concatenate((self.context, x))[None, :], 'h': self.h, 'c': self.c})
        self.context = x[-64:].copy()
        return float(p.reshape(-1)[0])


def wav(pcm):
    out = io.BytesIO()
    with wave.open(out, 'wb') as w:
        w.setparams((1, 2, SR, 0, 'NONE', ''))
        w.writeframes(pcm)
    return out.getvalue()


@web.middleware
async def authenticate(request, handler):
    # This protocol is for native clients, not browser pages. Loopback/private
    # binding alone does not prevent cross-origin WebSocket connections.
    if 'Origin' in request.headers:
        raise web.HTTPForbidden(text='Browser origins are not supported.')
    key = request.app['config'].get('api_key', '')
    if key and request.headers.get('Authorization') != 'Bearer ' + key:
        raise web.HTTPUnauthorized()
    return await handler(request)


async def health(request):
    app = request.app
    states = {}
    for name, url in [('stream', app['config']['stream_url'].split('/v1/')[0] + '/health'),
                      ('batch', app['config']['batch_url'].rsplit('/', 1)[0] + '/')]:
        if name == 'batch' and not app['config'].get('batch_enabled', True):
            continue
        try:
            async with app['http'].get(url, timeout=aiohttp.ClientTimeout(total=2)) as r:
                states[name] = r.status == 200
        except (aiohttp.ClientError, asyncio.TimeoutError):
            states[name] = False
    return web.json_response(dict(ok=any(states.values()), busy=app['busy'], **states),
                             status=200 if any(states.values()) else 503)


async def websocket(request):
    app = request.app
    ws = web.WebSocketResponse(heartbeat=15, max_msg_size=128*1024)
    await ws.prepare(request)
    if app['busy']:
        await ws.send_json(dict(type='error', text='Diktatdienst ist bereits belegt.'))
        await ws.close()
        return ws
    app['busy'] = True
    tasks = []
    send_lock = asyncio.Lock()
    cfg = app['config']
    batch_enabled = cfg.get('batch_enabled', True)
    pcm = bytearray()
    stream_final = None
    stream_failed = False
    upload_complete = False
    stopped = False
    results = []
    voiced = []
    batch_failed = False
    q = asyncio.Queue(maxsize=500)
    work = asyncio.Queue()
    seg = None

    async def emit(e):
        async with send_lock:
            await ws.send_json(e)

    try:
        msg = await asyncio.wait_for(ws.receive_json(), 10)
        if msg.get('type') != 'start':
            raise ValueError('Expected start message')
        segmentation = dict(msg.get('segmentation', {}))
        segmentation['language'] = msg.get('language', 'en')
        opts = Options(**segmentation)
        # Duration bound remains available when punctuation or a model fails.
        opts.signals = tuple(dict.fromkeys((*opts.signals, 'length')))
        seg = Segmenter(opts)
        chosen = 'combined'
        vad = Silero(app['vad'])

        async def events(items):
            for e in items:
                if e.get('type') == 'segment' and e.get('lane') == chosen:
                    if e['end'] > e['start']:
                        if batch_enabled:
                            work.put_nowait(dict(e, pcm=bytes(pcm[e['start']*2:e['end']*2]), has_speech=any(e['start'] <= t < e['end'] for t in voiced)))
                        await emit(e)

        async def upload():
            nonlocal upload_complete
            while True:
                data = await q.get()
                if data is None:
                    upload_complete = True
                    return
                yield (np.frombuffer(data, dtype='<i2').astype('<f4') / 32768).tobytes()

        async def streaming():
            nonlocal stream_final, stream_failed
            try:
                params = dict(model=cfg.get('stream_model', 'voxtral-stream'), sample_rate='16000',
                              channels='1', sample_format='f32le', language=msg.get('language', 'de'))
                async with app['http'].post(cfg['stream_url'], params=params, data=upload(),
                    headers={'Content-Type': 'application/octet-stream'},
                    timeout=aiohttp.ClientTimeout(total=None, connect=3, sock_read=30)) as r:
                    r.raise_for_status()
                    async for line in r.content:
                        if not line.startswith(b'data:'):
                            continue
                        raw = line[5:].strip()
                        if raw == b'[DONE]':
                            break
                        e = json.loads(raw)
                        if e.get('type') == 'error':
                            raise RuntimeError('Streaming backend error')
                        if e.get('type') == 'transcript.text.delta':
                            text = e.get('delta', '')
                            await events(seg.delta(text))
                            await emit(dict(type='delta', text=text))
                        elif e.get('type') == 'transcript.text.done':
                            if not stopped or not upload_complete:
                                raise RuntimeError('Streaming finalized before complete audio upload')
                            if e.get('text', '') != seg.text:
                                raise RuntimeError('Streaming final/delta mismatch')
                            stream_final = e.get('text', '')
                if stream_final is None:
                    raise RuntimeError('Streaming ended without final transcript')
            except (aiohttp.ClientError, asyncio.TimeoutError, RuntimeError, ValueError):
                stream_failed = True
                stream_final = None
                # Preserve committed cuts, then continue with pauses/length.
                seg.lanes[chosen].signals = ('vad', 'length')
                seg.lanes[chosen].combine = 'or'
                seg.lanes[chosen].pending = None
                await emit(dict(type='warning', text='Voxtral unavailable; continuing with speech pauses.' if batch_enabled else 'Voxtral unavailable; no complete streaming transcript.'))

        async def batch():
            nonlocal batch_failed
            while True:
                section = await work.get()
                if section is None:
                    return
                row = {k: v for k, v in section.items() if k != 'pcm'}
                try:
                    # VAD is a segmentation hint, not proof that audio is empty.
                    # A false negative must never silently discard a section.
                    if not any(section['pcm']):
                        row.update(text='', ok=True)
                        results.append(row)
                        await emit(dict(type='section_result', number=row['number'], text='', ok=True))
                        continue
                    if batch_failed:
                        raise RuntimeError('Batch circuit open')
                    form = aiohttp.FormData()
                    form.add_field('file', wav(section['pcm']), filename='section.wav', content_type='audio/wav')
                    for k, v in [('language', msg.get('language', 'de')), ('response_format', 'json'),
                                 ('temperature', '0'), ('temperature_inc', '0.2')]:
                        form.add_field(k, v)
                    if cfg.get('batch_model'):
                        form.add_field('model', cfg['batch_model'])
                    headers = {}
                    if cfg.get('batch_api_key'):
                        headers['Authorization'] = 'Bearer ' + cfg['batch_api_key']
                    async with app['http'].post(cfg['batch_url'], data=form, headers=headers,
                        timeout=aiohttp.ClientTimeout(total=float(msg.get('batch_timeout_s', 8)), connect=3)) as r:
                        r.raise_for_status()
                        answer = await r.json()
                        text = answer['text']
                        if not isinstance(text, str) or not text.strip():
                            raise ValueError('Empty transcript for a section with detected speech')
                        row.update(text=text.strip(), ok=True)
                except (aiohttp.ClientError, asyncio.TimeoutError, RuntimeError, ValueError, KeyError, TypeError):
                    batch_failed = True
                    row.update(text='', ok=False)
                results.append(row)
                await emit(dict(type='section_result', number=row['number'], text=row['text'], ok=row['ok']))

        st = asyncio.create_task(streaming())
        bt = asyncio.create_task(batch()) if batch_enabled else None
        tasks.append(st)
        if bt is not None:
            tasks.append(bt)
        await emit(dict(type='ready'))
        processed = 0
        loop = asyncio.get_running_loop()
        deadline = loop.time() + float(cfg.get('capture_max_seconds', 660))
        while True:
            remaining = deadline - loop.time()
            if remaining <= 0:
                raise RuntimeError('Recording wall-clock limit reached')
            try:
                m = await asyncio.wait_for(ws.receive(), min(remaining, float(cfg.get('capture_idle_seconds', 15))))
            except asyncio.TimeoutError as exc:
                raise RuntimeError('Recording timed out waiting for audio or stop') from exc
            if m.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                break
            if m.type == aiohttp.WSMsgType.BINARY:
                if len(m.data) % 2:
                    raise ValueError('Odd PCM byte count')
                pcm.extend(m.data)
                if len(pcm) > SR*2*600:
                    raise ValueError('Ten minute recording limit')
                while len(pcm) - processed >= 1024:
                    prob = vad.frame(bytes(pcm[processed:processed+1024]))
                    processed += 1024
                    if prob >= .5:
                        voiced.append(processed//2-512)
                    await events(seg.audio(processed//2, prob))
                if not stream_failed:
                    try:
                        q.put_nowait(m.data)
                    except asyncio.QueueFull:
                        # Failing explicitly is safer than silently dropping audio.
                        raise RuntimeError('Streaming audio backlog')
                if st.done() and stream_final is not None:
                    # A backend that finalizes before capture ends is incomplete.
                    stream_final = None
                    stream_failed = True
            elif m.type == aiohttp.WSMsgType.TEXT and m.data == 'stop':
                stopped = True
                if not st.done():
                    await asyncio.wait_for(q.put(None), 3)
                    try:
                        await asyncio.wait_for(st, float(msg.get('stream_final_timeout_s', 5)))
                    except asyncio.TimeoutError:
                        stream_final = None
                        stream_failed = True
                await events(seg.finish(len(pcm)//2))
                if bt is not None:
                    work.put_nowait(None)
                    try:
                        await asyncio.wait_for(bt, float(msg.get('finish_timeout_s', 25)))
                    except asyncio.TimeoutError:
                        batch_failed = True
                if not batch_enabled:
                    if stream_final is None:
                        raise RuntimeError('No complete Voxtral transcript; file ASR fallback required.')
                    text, source = stream_final.strip(), 'voxtral'
                elif not batch_failed and results:
                    text = ' '.join(r['text'] for r in results if r['text'])
                    source = 'whisper'
                elif stream_final is not None and msg.get('stream_fallback', True):
                    # Use the complete stream once, never splice estimated words.
                    text, source = stream_final.strip(), 'voxtral-fallback'
                else:
                    raise RuntimeError('No complete transcript; file ASR fallback required.')
                if not text.strip() and stream_final and msg.get('stream_fallback', True):
                    text, source = stream_final.strip(), 'voxtral-fallback'
                if not text.strip() and voiced:
                    raise RuntimeError('Speech detected but ASR returned no text; file ASR fallback required.')
                await emit(dict(type='result', text=text, source=source, sections=len(results), samples=len(pcm)//2))
                break
        if not stopped:
            return ws
    except Exception as e:
        if not ws.closed:
            with contextlib.suppress(Exception):
                await emit(dict(type='error', text=str(e)[:200]))
    finally:
        for task in tasks:
            task.cancel()
        for task in tasks:
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
        pcm.clear()
        app['busy'] = False
        await ws.close()
    return ws


def create_app(config):
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = opts.inter_op_num_threads = 1
    app = web.Application(middlewares=[authenticate])
    app['vad'] = ort.InferenceSession(config['vad_model'], sess_options=opts, providers=['CPUExecutionProvider'])
    if [x.name for x in app['vad'].get_inputs()] != ['input', 'h', 'c']:
        raise ValueError('Expected Silero v6 ONNX with input/h/c')
    app['config'] = config
    app['busy'] = False
    async def start(a):
        a['http'] = aiohttp.ClientSession()
    async def stop(a):
        await a['http'].close()
    app.on_startup.append(start)
    app.on_cleanup.append(stop)
    app.router.add_get('/health', health)
    app.router.add_get('/ws', websocket)
    return app


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    with open(args.config) as f:
        config = json.load(f)
    web.run_app(create_app(config), host=config.get('host', '127.0.0.1'), port=config.get('port', 8960))
