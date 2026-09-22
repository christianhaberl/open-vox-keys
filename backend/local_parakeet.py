"""Optional loopback-only NeMo/CUDA fallback. Load on demand, release after idle.

Run in an existing supported NeMo environment with a local .nemo checkpoint.
No cloud calls, model downloads or saved recordings. Not a Surface/NPU adapter.
"""
import argparse
import gc
import json
import os
import tempfile
import threading
import time
from email import policy
from email.parser import BytesParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

lock = threading.Lock()
model = None
keep_warm = False
last_used = time.monotonic()


def load():
    global model, last_used
    if model is None:
        import torch
        from nemo.collections.asr.models import ASRModel
        if not torch.cuda.is_available():
            raise RuntimeError('CUDA unavailable')
        model = ASRModel.restore_from(args.model, map_location='cpu').cuda().eval()
    last_used = time.monotonic()
    return model


def idle_cleanup():
    global model
    while True:
        time.sleep(15)
        if lock.acquire(blocking=False):
            try:
                if model is not None and not keep_warm and time.monotonic()-last_used > args.idle_seconds:
                    model = None
                    gc.collect()
                    import torch
                    torch.cuda.empty_cache()
            finally:
                lock.release()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, data):
        raw = json.dumps(data).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if self.path == '/health':
            self.reply(200, dict(ok=True, loaded=model is not None, keep_warm=keep_warm))
        else:
            self.reply(404, dict(error='Not found'))

    def do_POST(self):
        global keep_warm, last_used
        try:
            # Native-client endpoint: browser pages must not control model
            # residency or start inference on a loopback service.
            if 'Origin' in self.headers:
                return self.reply(403, dict(error='Browser origins are not supported'))
            content_type = self.headers.get_content_type()
            expected = {'/warm': 'application/json',
                        '/v1/audio/transcriptions': 'multipart/form-data'}.get(self.path)
            if expected is None:
                return self.reply(404, dict(error='Not found'))
            if content_type != expected:
                return self.reply(415, dict(error='Unsupported content type'))
            size = int(self.headers.get('Content-Length', '0'))
            if size <= 0 or size > 20*1024*1024:
                return self.reply(413, dict(error='Invalid request size'))
            data = self.rfile.read(size)
            if self.path == '/warm':
                value = json.loads(data)
                with lock:
                    keep_warm = bool(value.get('keep_warm', False))
                    if keep_warm:
                        load()
                return self.reply(200, dict(loaded=model is not None, keep_warm=keep_warm))
            if self.path != '/v1/audio/transcriptions':
                return self.reply(404, dict(error='Not found'))
            message = BytesParser(policy=policy.default).parsebytes(
                ('Content-Type: '+self.headers.get('Content-Type', '')+'\r\nMIME-Version: 1.0\r\n\r\n').encode()+data)
            audio = next(p.get_payload(decode=True) for p in message.iter_parts()
                         if p.get_param('name', header='Content-Disposition') == 'file')
            with lock:
                import torch
                m = load()
                # NeMo accepts a filename; remove the private temporary file even on error.
                name = None
                try:
                    with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as f:
                        f.write(audio)
                        name = f.name
                    with torch.inference_mode():
                        answer = m.transcribe([name], batch_size=1, num_workers=0)
                    item = answer[0]
                    if isinstance(item, (tuple, list)):
                        item = item[0]
                    text = item.text if hasattr(item, 'text') else str(item)
                finally:
                    if name:
                        os.unlink(name)
                    last_used = time.monotonic()
            self.reply(200, dict(text=text))
        except Exception as e:
            self.reply(500, dict(error=type(e).__name__))


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--model', required=True)
    p.add_argument('--port', type=int, default=8961)
    p.add_argument('--idle-seconds', type=int, default=300)
    args = p.parse_args()
    threading.Thread(target=idle_cleanup, daemon=True).start()
    ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
