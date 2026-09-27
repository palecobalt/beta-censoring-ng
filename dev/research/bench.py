#!/usr/bin/env python3
"""Throwaway benchmark: censor a short clip frame-by-frame through Beta Censoring.
Runs on the Docker host; ffmpeg runs via `docker exec` in long-lived container clipresearch-ff."""
import base64, concurrent.futures as cf, json, os, shutil, statistics, subprocess, sys, time
import requests

W = "/tmp/clipresearch"
API = "http://127.0.0.1:2382/censoring/censorImage"
OPTS = json.load(open(f"{W}/censor-options.json"))
CT = "clipresearch-ff"
WORKERS = 4
SAMPLE_FPS = 5


def ff(args):
    t = time.monotonic()
    r = subprocess.run(["docker", "exec", "-w", "/w", CT, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y"] + args,
                       capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError(r.stderr[-2000:])
    return time.monotonic() - t


def probe(clip):
    r = subprocess.run(["docker", "exec", "-w", "/w", "--", CT, "ffprobe", "-v", "error", "-select_streams", "v:0",
                        "-count_packets", "-show_entries", "stream=width,height,r_frame_rate,nb_read_packets",
                        "-of", "json", clip], capture_output=True, text=True)
    s = json.loads(r.stdout)["streams"][0]
    num, den = s["r_frame_rate"].split("/")
    return s["width"], s["height"], float(num) / float(den), int(s["nb_read_packets"])


def censor_one(src, dst, session):
    data = open(src, "rb").read()
    body = {"imageDataUrl": "data:image/jpeg;base64," + base64.b64encode(data).decode(), "censorOptions": OPTS}
    for attempt in range(30):
        try:
            t = time.monotonic()
            r = session.post(API, json=body, timeout=120)
            r.raise_for_status()
            open(dst, "wb").write(r.content)
            return time.monotonic() - t, r.content == data
        except (requests.ConnectionError, requests.Timeout, requests.HTTPError) as e:
            print(f"  retry {src}: {e}", file=sys.stderr, flush=True)
            time.sleep(5)
    raise RuntimeError("API unavailable")


def censor_dir(src_dir, dst_dir):
    os.makedirs(dst_dir, exist_ok=True)
    files = sorted(os.listdir(src_dir))
    t = time.monotonic()
    sessions = [requests.Session() for _ in range(WORKERS)]
    with cf.ThreadPoolExecutor(WORKERS) as ex:
        res = list(ex.map(lambda i: censor_one(f"{src_dir}/{files[i]}", f"{dst_dir}/{files[i]}", sessions[i % WORKERS]),
                          range(len(files))))
    wall = time.monotonic() - t
    lat = [x[0] for x in res]
    return {"n": len(files), "wall": round(wall, 2), "fps": round(len(files) / wall, 2),
            "lat_med": round(statistics.median(lat), 3), "unchanged": sum(x[1] for x in res)}


def fresh(d):
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)


def main(clip, modes):
    name = clip.rsplit(".", 1)[0]
    w, h, fps, n = probe(clip)
    out = {"clip": clip, "w": w, "h": h, "fps": fps, "frames": n, "dur": round(n / fps, 2)}
    base = f"{W}/run_{name}"
    fresh(base)
    rel = f"run_{name}"
    # baseline: plain transcode, no censoring
    out["transcode_x264"] = round(ff(["-i", clip, "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "copy",
                                      "-movflags", "+faststart", f"{rel}/plain_x264.mp4"]), 2)
    out["transcode_nvenc"] = round(ff(["-i", clip, "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23", "-c:a", "copy",
                                       "-movflags", "+faststart", f"{rel}/plain_nvenc.mp4"]), 2)
    if "all" in modes:
        fresh(f"{base}/frames"); fresh(f"{base}/cens")
        out["a_decode_jpeg"] = round(ff(["-i", clip, "-q:v", "3", f"{rel}/frames/%05d.jpg"]), 2)
        out["a_rest"] = censor_dir(f"{base}/frames", f"{base}/cens")
        common = ["-framerate", str(fps), "-i", f"{rel}/cens/%05d.jpg", "-i", clip, "-map", "0:v", "-map", "1:a?",
                  "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart"]
        out["a_encode_x264"] = round(ff(common[:6] + common[6:] + ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                                                                   f"{rel}/cens_x264.mp4"]), 2)
        out["a_encode_nvenc"] = round(ff(common + ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23",
                                                   f"{rel}/cens_nvenc.mp4"]), 2)
    if "sample" in modes:
        fresh(f"{base}/samp"); fresh(f"{base}/samp_c")
        out["b_decode_sampled"] = round(ff(["-i", clip, "-vf", f"fps={SAMPLE_FPS}", "-q:v", "3", f"{rel}/samp/%05d.jpg"]), 2)
        out["b_rest_sampled"] = censor_dir(f"{base}/samp", f"{base}/samp_c")
        fresh(f"{base}/samps"); fresh(f"{base}/samps_c")
        out["b_decode_sampled_640"] = round(ff(["-i", clip, "-vf", f"fps={SAMPLE_FPS},scale='min(640,iw)':-2", "-q:v", "3",
                                                f"{rel}/samps/%05d.jpg"]), 2)
        out["b_rest_sampled_640"] = censor_dir(f"{base}/samps", f"{base}/samps_c")
        # render pass: decode original, blur 3 reused boxes (each 20% x 25% of frame) + 1 black bar, encode
        bw, bh = w // 5, h // 4
        g = (f"[0:v]split=4[base][a][b][c];"
             f"[a]crop={bw}:{bh}:{w//10}:{h//8},boxblur=20:2[ab];"
             f"[b]crop={bw}:{bh}:{w//2}:{h//3},boxblur=20:2[bb];"
             f"[c]crop={bw}:{bh}:{w//3}:{h//2},boxblur=20:2[cb];"
             f"[base][ab]overlay={w//10}:{h//8}[t1];[t1][bb]overlay={w//2}:{h//3}[t2];[t2][cb]overlay={w//3}:{h//2},"
             f"drawbox=x={w//4}:y={h//4}:w={bw}:h={bh//3}:color=black:t=fill,format=yuv420p[v]")
        for enc, args in (("x264", ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23"]),
                          ("nvenc", ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23"])):
            out[f"b_render_{enc}"] = round(ff(["-i", clip, "-filter_complex", g, "-map", "[v]", "-map", "0:a?", "-c:a", "copy",
                                               "-movflags", "+faststart"] + args + [f"{rel}/boxes_{enc}.mp4"]), 2)
    print(json.dumps(out), flush=True)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2].split(",") if len(sys.argv) > 2 else ["all", "sample"])
