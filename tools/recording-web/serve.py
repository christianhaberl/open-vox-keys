from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
import json, uuid, datetime, os
ROOT=Path(__file__).resolve().parent
DATA=Path(os.environ["LOCALAPPDATA"]) if "LOCALAPPDATA" in os.environ else Path.home()/".local/share"
OUT=Path(os.environ.get("OVK_RECORDINGS",str(DATA/"OpenVoxKeys/Tests")))
class Handler(SimpleHTTPRequestHandler):
 def __init__(self,*a,**kw): super().__init__(*a,directory=str(ROOT),**kw)
 def do_POST(self):
  if self.path!="/recording": self.send_error(404);return
  if self.headers.get("Origin") not in ("http://localhost:8097","http://127.0.0.1:8097"): self.send_error(403);return
  try: size=int(self.headers.get("Content-Length","0"))
  except ValueError: self.send_error(400);return
  if not 0<size<=128*1024*1024: self.send_error(413);return
  mime=self.headers.get("Content-Type","")
  ext="webm" if mime.startswith("audio/webm") else "ogg" if mime.startswith("audio/ogg") else "m4a" if mime.startswith("audio/mp4") else None
  if not ext:self.send_error(415);return
  data=self.rfile.read(size)
  if len(data)!=size:self.send_error(400);return
  ident=datetime.datetime.now().strftime("%Y%m%d-%H%M%S")+"-"+uuid.uuid4().hex[:6]
  dest=OUT/ident;dest.mkdir(parents=True)
  (dest/("recording."+ext)).write_bytes(data)
  prompts=json.loads((ROOT/"prompts.json").read_text())
  (dest/"reference.txt").write_text("\n\n".join(prompts),encoding="utf-8")
  (dest/"session.json").write_text(json.dumps({"id":ident,"audio":"recording."+ext,"mime":mime,"prompts":prompts,"reference_status":"reading_script_not_verified_ground_truth","status":"capture_only"},ensure_ascii=False,indent=2),encoding="utf-8")
  body=json.dumps({"id":ident}).encode();self.send_response(200);self.send_header("Content-Type","application/json");self.send_header("Content-Length",str(len(body)));self.end_headers();self.wfile.write(body)
print("Open Vox Keys: http://localhost:8097",flush=True)
ThreadingHTTPServer(("127.0.0.1",8097),Handler).serve_forever()
