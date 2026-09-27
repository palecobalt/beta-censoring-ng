#!/usr/bin/env python3
"""Throwaway: acts as an HTTP forward proxy (and origin) for Firefox, logs Range handling of <video>.
Modes (path prefix): /range/ honours Range with 206; /strip/ ignores Range -> full 200, no Accept-Ranges;
/lying/ ignores Range -> full 200 but advertises Accept-Ranges: bytes. Body is throttled."""
import json, os, re, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

D = os.path.dirname(os.path.abspath(__file__))
RATE = 1_500_000  # bytes/s
PAGE = """<!doctype html><meta charset=utf-8><body><script>
const cases = [["range","clipB_720p.mp4"],["strip","clipB_720p.mp4"],["lying","clipB_720p.mp4"],["strip","clipB_moovend.mp4"]];
function rep(o){ return fetch("/report", {method:"POST", body: JSON.stringify(o)}); }
function ranges(r){ let a=[]; for(let i=0;i<r.length;i++) a.push([+r.start(i).toFixed(2), +r.end(i).toFixed(2)]); return a; }
function once(el, ev, ms){ return new Promise(res=>{ let t=setTimeout(()=>res("timeout"), ms); el.addEventListener(ev, ()=>{clearTimeout(t); res(ev);}, {once:true}); }); }
async function run(mode, file){
  const v = document.createElement("video"); v.muted = true; v.preload = "auto"; document.body.appendChild(v);
  const t0 = performance.now(); let err=null; v.onerror = ()=>{ err = v.error && v.error.code; };
  v.src = "/" + mode + "/" + file + "?" + Math.random();
  const meta = await once(v, "loadedmetadata", 20000);
  const tMeta = performance.now()-t0;
  let p = v.play().then(()=>"played").catch(e=>"play-fail:"+e.name);
  await once(v, "playing", 10000);
  await new Promise(r=>setTimeout(r, 700));
  const before = {seekable: ranges(v.seekable), buffered: ranges(v.buffered), dur: v.duration};
  v.currentTime = 11.5; const tSeek = performance.now();
  const s = await once(v, "seeked", 20000);
  const after = {ev: s, currentTime: +v.currentTime.toFixed(2), seekMs: Math.round(performance.now()-tSeek), buffered: ranges(v.buffered)};
  await new Promise(r=>setTimeout(r, 1500));
  await rep({mode, file, meta, tMeta: Math.round(tMeta), play: await p, before, after, err, endTime: +v.currentTime.toFixed(2), readyState: v.readyState});
  v.removeAttribute("src"); v.load(); v.remove();
}
(async()=>{ for (const [m,f] of cases) await run(m,f); await rep({done:true}); })();
</script>"""

LOG = []
DONE = threading.Event()


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("content-length", 0))
        body = json.loads(self.rfile.read(n))
        print("REPORT", json.dumps(body), flush=True)
        if body.get("done"):
            DONE.set()
        self.send_response(204); self.send_header("content-length", "0"); self.end_headers()

    def do_GET(self):
        u = urlparse(self.path)
        rng = self.headers.get("range")
        if u.path in ("/", "/test.html"):
            b = PAGE.encode()
            self.send_response(200); self.send_header("content-type", "text/html"); self.send_header("content-length", str(len(b)))
            self.end_headers(); self.wfile.write(b); return
        m = re.match(r"^/(range|strip|lying)/(clipB_\w+\.mp4)$", u.path)
        if not m:
            self.send_response(404); self.send_header("content-length", "0"); self.end_headers(); return
        mode, f = m.groups()
        data = open(os.path.join(D, f), "rb").read()
        start, end, code = 0, len(data) - 1, 200
        if mode == "range" and rng:
            mm = re.match(r"bytes=(\d+)-(\d*)", rng)
            start = int(mm.group(1)); end = int(mm.group(2)) if mm.group(2) else len(data) - 1
            code = 206
        print(f"REQ {mode}/{f} proxied={self.path.startswith('http')} range={rng!r} -> {code} bytes {start}-{end}", flush=True)
        self.send_response(code)
        self.send_header("content-type", "video/mp4")
        self.send_header("content-length", str(end - start + 1))
        if code == 206:
            self.send_header("content-range", f"bytes {start}-{end}/{len(data)}")
        if mode in ("range", "lying"):
            self.send_header("accept-ranges", "bytes")
        self.end_headers()
        sent, t0, chunk = start, time.monotonic(), 64000
        try:
            while sent <= end:
                self.wfile.write(data[sent:min(sent + chunk, end + 1)]); sent += chunk
                delay = (sent - start) / RATE - (time.monotonic() - t0)
                if delay > 0:
                    time.sleep(delay)
            print(f"  END {mode}/{f} range={rng!r} complete", flush=True)
        except (BrokenPipeError, ConnectionResetError):
            print(f"  ABORT {mode}/{f} range={rng!r} after {sent - start} bytes", flush=True)


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    DONE.wait(float(sys.argv[2]) if len(sys.argv) > 2 else 240)
    print("server exiting", flush=True)
