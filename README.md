# beta-censoring-ng

A continuation of silveredgold's [beta-censoring](https://github.com/silveredgold/beta-censoring)
server and [censor-core](https://github.com/silveredgold/censor-core) library (both GPL-3.0, no
longer updated since 2023). It runs the NudeNet v3 models (`320n`, `640m`) as well as the old v2
detector, and adds GPU inference, detection-only endpoints, a censoring proxy that works without
the browser extension, and video clip censoring. Beta Protection works unchanged.

Both upstream repositories are included with their full history, in `censor-core/` and
`beta-censoring/`.

## What changed

- **ONNX Runtime 1.12.1 → 1.26.0.** v3 models are ONNX IR version 10; 1.12 only loads up to 8
  (the "Unsupported model IR version" failure people hit when trying 640m).
- **v3 model support in `AIService`.** v3 models are detected from their output shape.
  Preprocessing and decoding mirror NudeNet's reference Python: longest side scaled to 320/640,
  anchored top-left, BGR/255 input, YOLOv8 output decoding and class-agnostic NMS (0.25 / 0.45).
- **v3 labels are renamed to the v2 names** (`FACE_FEMALE` → `FACE_F`,
  `FEMALE_BREAST_COVERED` → `COVERED_BREAST_F`, ...), so clients and config files keep working.
  v3 adds `COVERED_ANUS` and `COVERED_ARMPITS`, which Beta Protection doesn't request.
- **Lower default thresholds for v3.** v3 scores run lower than v2's: `MinimumScore` 0.35,
  exposed female/male-specific classes 0.30, covered female-specific classes 0.45.
- **`ModelPath` setting** (`BCS_ModelPath` env var, `--ModelPath`, or `ModelPath:` in `config.yml`).
- **Detection-only REST endpoints.** `POST /censoring/detect` (`{imageDataUrl, censorOptions}`)
  and `POST /censoring/detectBatch` (`{imageDataUrls: [...], censorOptions}`) return
  `{width, height, results: [{label, confidence, box}]}` without censoring. Boxes get the same
  scaling and merging as `censorImage` unless `transform: false` is sent.
- **Model auto-download** fetches `640m.onnx` (or `320n.onnx` with `Model: 320n`) from NudeNet's
  `v3.4-weights` release into the server's folder and checks its SHA-256 (it used to find nothing
  and abort with "Failed to retrieve AI model!"). When no model can be found or loaded, the server
  says why and exits instead of failing on the first image.
- **Only local connections by default,** and web pages can't use the server; see "Network access".
- **Animated GIFs and WebP images are censored frame by frame,** through the REST API and through
  the SignalR hub Beta Protection uses (see "With Beta Protection" for its part).
- **Sticker packs work:** stickers used to be almost never drawn (only the blur behind them).
- **Image downloads** send browser headers and a Referer (sites with hotlink protection answered
  403), and protocol-relative `//host/path` URLs work.
- The server builds against the local censor-core source instead of the NuGet packages.
- **.NET 10** (from .NET 6, which is out of support) with current dependencies: ImageSharp 3.1 and
  ImageSharp.Drawing 2.1 (fixes seven published ImageSharp vulnerabilities), LiteDB 5.0.21, MediatR 12.5,
  Swashbuckle 10. Sticker packs (`*.betapkg`, `*-stickers.zip`) are read with .NET's own zip support instead
  of Lexical.FileProvider/SharpCompress.

## Test results

On a 4 vCPU VM with 17 non-explicit Wikimedia Commons photos (beach volleyball, swimwear, yoga,
barefoot runners):

- **Matches the official Python NudeNet v3.** 640m: all 92 Python detections found, mean box IoU
  0.91, mean score difference 0.025. 320n: 42 of 46, the rest right at the 0.25 cutoff.
- **Speed (model only, per image):** v2 0.3–4.5 s, v3 320n 40–90 ms, v3 640m 330–700 ms.
- **End to end** through the REST API and the SignalR hub Beta Protection uses, with optimization
  mode Normal and None: censoring lands on the right regions.
- **Also tested with v0.3.0:** a Linux build, the Docker stack on a CPU, and Beta Protection in a
  browser (photos, stickers, animated GIF and WebP, and a web page trying to use the server).
- **Tested with v0.2.0 only:** the Windows zip on Windows 11, and the GPU image with CUDA on a
  GeForce card.
- **Not tested:** accuracy on explicit images, the macOS build, running as a service (Windows or
  systemd), DirectML.

Observations from the sample: all three models (v2, 320n, 640m) labelled some men's faces
`FACE_F`. 320n and v2 flagged bikini areas as `EXPOSED_*` at 0.3–0.6; 640m labelled them covered.

## Models

The NudeNet model weights are not part of this repository. NudeNet's
[v3.4-weights release](https://github.com/notAI-tech/NudeNet/releases/tag/v3.4-weights) has two:

| Model | Size | Per image on a CPU | Notes |
|---|---|---|---|
| `640m.onnx` (default) | 104 MB | 0.3–0.7 s | more accurate; told swimwear apart from exposed skin in the tests |
| `320n.onnx` | 12 MB | 40–90 ms | for slow computers or image-heavy pages; misses more and over-censors swimwear |

The server uses the model file it finds in its own folder, the folder it is started in, or the
system temp folder (`640m` before `320n` before any other `.onnx` file), and downloads `640m.onnx`
into its own folder when it finds none. `Model: 320n` (or `640m`) in `config.yml`, or the
`BCS_Model` environment variable, makes it use and download that model only; `ModelPath` points at
a specific file or folder. `scripts/fetch-models.sh` downloads both models into `models/` for the
Docker build, and checks their SHA-256 as the server does. NudeNet and its models are published by
[notAI-tech](https://github.com/notAI-tech/NudeNet) under the AGPL-3.0.

## Run from a release download

The [releases page](https://github.com/palecobalt/beta-censoring-ng/releases) has self-contained
builds for Windows (x64), Linux (x64) and macOS (Apple Silicon); no .NET installation is needed.

1. Download the zip for your system and unpack it.
2. Start `BetaCensor.Server.exe` (Windows) or `./BetaCensor.Server` (Linux, macOS).
3. In Beta Protection's settings set **Backend Host** to `http://localhost:2382`, select
   **Beta Censoring**, and click **Save and Reconnect**.

At its first start the server downloads the `640m` model (104 MB) from NudeNet's releases into
the unpacked folder. If that fails (no internet, or GitHub's limit of 60 requests an hour per IP
address, which VPN addresses can reach), download `640m.onnx` from the
[v3.4-weights release](https://github.com/notAI-tech/NudeNet/releases/tag/v3.4-weights) yourself
and put it in the unpacked folder. On a slow computer, or if pages with many images take too long,
use the small `320n` model instead: put `Model: 320n` in a `config.yml` in the unpacked folder
(the server reads it from the folder it is started in), or set the `BCS_Model` environment
variable, and the server downloads that one. See "Models" for the differences. An install from
before v0.4.0 keeps using its `320n` model until `Model: 640m` is set or the model file is
deleted.

**With Beta Protection:**

- GIFs only reach the server when **Animate GIFs** is on in Beta Protection's settings; otherwise
  the extension handles them as video.
- Beta Protection turns images it can read inside the page (from the page's own site, or served
  with CORS headers) into a still JPEG before sending them, so animations among those arrive as one
  frame. Images from other sites, which is most of them, arrive as they are, and animations are
  censored frame by frame.

On macOS the downloaded files are quarantined; clear that with
`xattr -dr com.apple.quarantine <unpacked folder>`. Intel Macs aren't covered, because ONNX Runtime
no longer publishes macOS x64 builds; use Docker there.

## Run with Docker

```bash
scripts/fetch-models.sh
docker compose up -d --build
```

Then in Beta Protection's settings set **Backend Host** to `http://localhost:2382`, select
**Beta Censoring**, and click **Save and Reconnect**. `docker-compose.yml` publishes the server
(and the video service) on `127.0.0.1` only; to reach it from other machines, change the port to
`"2382:2382"` (see "Network access").

- Model: `BCS_ModelPath=/app/models/640m.onnx` (default) or `/app/models/320n.onnx`.
- Workers: `BCS_Server__WorkerCount` (about half the host's cores).
- Other settings: copy `config.example.yml` to `config.yml` and mount it at `/app/config.yml`.
  Environment variables override the file.


## GPU (NVIDIA)

`docker-compose.gpu.yml` switches the server to a CUDA build (`Dockerfile.gpu`):

```bash
docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d --build
```

or put `COMPOSE_FILE=docker-compose.yml:docker-compose.gpu.yml` in a `.env` file so plain
`docker compose` commands use it.

- **Requirements:** an NVIDIA driver with CUDA 12 support and the NVIDIA container toolkit.
  ONNX Runtime 1.26 is the newest release built for CUDA 12; 1.27 and later need CUDA 13
  (driver 580+). The image removes NVIDIA's forward-compatibility `libcuda`, which only works on
  datacenter GPUs and fails with "CUDA failure 804" on GeForce cards.
- **Fallback:** if CUDA can't start, the server logs a warning and runs on the CPU. Check with
  `docker logs beta-censoring | grep CUDA`.
- **GPU memory** (640m on an RTX 3060 Ti): about 400 MiB after the first image, levelling off
  around 650 MiB under sustained load.
- **Speed on an RTX 3060 Ti host:** 24 simultaneous images in about 5 s (roughly 4.5 images/s) versus about 12.5 s
  on the CPU. Image decoding, the person-cropping pre-pass and the censoring itself still run on
  the CPU.

Settings (environment variables, or the `Server:` section of `config.yml`):

- `BCS_Server__GpuMaxConcurrentRuns` (default `1`): model runs on the GPU at once. Two or more gave
  no speed-up on the 3060 Ti and eventually broke the GPU context ("CUDA failure 716"), after
  which every request fails until the server restarts. `0` uses the .NET thread pool, which also
  makes GPU memory grow with the number of threads.
- `BCS_Server__GpuMemoryLimitMB`: caps ONNX Runtime's GPU memory pool, but requests that need
  more fail instead of waiting, so normally leave it unset.
- `GPU_DEVICE_ID` (in `.env`): which GPU the container gets on multi-GPU hosts, as an index or
  UUID from `nvidia-smi -L` (default 0). The container then sees only that GPU, as device 0.
- `BCS_Server__GpuDeviceId`: which GPU the server uses when it can see several (for example when
  run without Docker).

## Censoring proxy (no extension)

`docker compose` also starts `censor-proxy` on port 8080: a [mitmproxy](https://mitmproxy.org)
instance whose add-on (`proxy/censor_proxy.py`) sends every image response to Beta Censoring and
hands the censored image to the browser. Any browser can use it, but most sites are HTTPS, so the
proxy has to decrypt traffic with its own certificate authority, and the browser must trust it.

**Firefox setup** (Firefox has its own certificate store, so this affects only Firefox):

1. Settings → Network Settings → Manual proxy configuration: HTTP Proxy `<docker-host>`, port
   `8080`, tick "Also use this proxy for HTTPS".
2. With the proxy set, open `http://mitm.it` and download the Firefox certificate.
3. Settings → Privacy & Security → Certificates → View Certificates → Authorities → Import, and
   tick "Trust this CA to identify websites".

**What gets censored** is set in `proxy/censor-options.json` (class name → `censorType` and
`level`). The file is reloaded automatically when it changes. Censor types: `blur`, `pixelate`,
`blackbars`, `sticker:<category>`, `caption`. Classes: see "API".

**Merging boxes:** set `MergeOverlapping: true` under `CensorOptions` in `config.yml` (then
`docker compose restart beta-censoring`) to combine overlapping censored areas of any body part
into one box, as long as they use the same censor type. The combined box uses the settings of the
body part with the highest `level`. `MergeDistance: 0.1` also merges boxes within 10% of their size
of each other.

**Proxy settings** (environment variables in `docker-compose.yml`):

- `CENSOR_ON_ERROR`: `block` (default) replaces images that couldn't be censored (server error,
  timeout, AVIF/JPEG XL) with a grey placeholder; `pass` lets them through.
- `CENSOR_MIN_BYTES`: images smaller than this are passed through untouched (default 4000).
- `CENSOR_CONCURRENCY`, `CENSOR_TIMEOUT`: images censored at once, seconds per image.
- `VIDEO_POLICY`: `block` (default) answers video that can't be censored (adaptive streams such as
  Reddit and most X or YouTube video, clips over the limits, and failures) with an empty 403, so it
  doesn't play; `pass` streams it through uncensored.
- `VIDEO_CENSOR_URL`: the video service (`http://video-censor:2383`); set it empty to turn video
  censoring off, which sends all video to `VIDEO_POLICY`.
- `VIDEO_MAX_MB` (25), `VIDEO_TIMEOUT` (120), `VIDEO_CACHE_MB` (256): the largest clip to censor (keep
  it in line with the video service's limit), seconds to wait for the video service, and memory for
  censored clips kept for repeat and range requests.
- `PROXY_PORT` (in `.env`): the host port the proxy is published on, if 8080 is already taken
  (default 8080).

**Behaviour and limits:**

- The proxy asks sites for JPEG/WebP instead of AVIF/JPEG XL, which Beta Censoring can't read.
- Animated GIFs and WebP images are censored frame by frame: the model runs on a frame every
  200 ms of animation time and frames in between reuse the nearby boxes (see the animation settings in
  `config.example.yml`). On an RTX 3060 Ti host a 35–81 frame GIF takes about 3 s, and it only appears once every
  frame is done.
- Short video clips are censored by `video-censor` (see "Video clip censoring" below). A clip only
  starts playing once it has been downloaded and censored, typically 5–15 s for a clip of up to
  20 s. Longer videos and adaptive streams follow `VIDEO_POLICY`.
- Pages wait for each image to be censored, so image-heavy pages load noticeably slower.
- The proxy can read everything that passes through it, and anyone holding its CA key (stored in
  the `mitmproxy-ca` Docker volume) could impersonate sites to browsers that trust it. Keep port
  8080 on your LAN, and consider not routing banking or similar sites through it (for example with
  a proxy-switching extension, or mitmproxy's `ignore_hosts` option).

## Video clip censoring

`docker compose` also starts `video-censor` on port 2383 (published on `127.0.0.1`), which
censors short video clips for the proxy. The proxy sends it complete MP4, WebM and Ogg files up to `VIDEO_MAX_MB`, and answers the
browser with the censored clip once it's done, as one complete response without byte ranges.
Censored clips are kept in memory, so replays and range requests don't go back to the origin.
Everything else that's video follows `VIDEO_POLICY` (see the proxy settings).

`POST /censor` with the clip as the request body (MP4/QuickTime, WebM/Matroska or Ogg)
and the censor options as JSON in an `X-Censor-Options` header returns the clip as H.264 MP4 with
the censoring drawn on. Clips with nothing to censor come back unchanged; `X-Censored` says which,
and `X-Video-Censor-Stats` has timings. Errors: 413 over the limits, 422 not a readable video,
502 Beta Censoring unreachable.

How it works:

- The model runs on 5 frames per second, scaled to 640 px, through Beta Censoring's
  `/censoring/detectBatch`, with the same box scaling and merging as images.
- Every frame is censored with the boxes from the samples up to two sample intervals either side
  of it, plus boxes moved along with anything that moves between samples, all enlarged by 15% on
  each side. Something the model misses on one or two samples in a row stays covered unless it
  moves out of its last box. Fast movement the model loses track of can still be uncovered briefly.
- Blur, pixelation and black bars match the strength of the image effects at each level; stickers
  and captions are drawn as blur. Unlike on images, blur and pixelation cover the whole box and
  fade out just outside it.
- Frames are re-encoded with NVENC on NVIDIA GPUs (x264 otherwise), keeping the audio and putting
  the MP4 index first so playback can start straight away.
- One clip is processed at a time.

Settings (environment variables on `video-censor`):

- `VIDEO_MAX_MB` (25), `VIDEO_MAX_SECONDS` (20): larger or longer clips are refused with 413.
- `VIDEO_MAX_SHORT_SIDE` (1080), `VIDEO_MAX_FPS` (30): bigger or faster videos are scaled down.
- `VIDEO_SAMPLE_FPS` (5), `VIDEO_SAMPLE_SIZE` (640): how often, and at what size, frames go
  through the model.
- `VIDEO_HOLD_SAMPLES` (2), `VIDEO_MOTION_PADDING` (0.15): how many sample intervals matches carry
  over, and how much boxes grow on each side. Lower values censor less background around moving
  parts but uncover more when the model misses something.
- `VIDEO_ENCODER` (`auto`): `nvenc` or `x264` to force one.
- `VIDEO_DETECT_BATCH` (16), `VIDEO_DETECT_CONCURRENCY` (2), `VIDEO_DETECT_TIMEOUT` (120): frames
  per detection request, requests at once, and seconds per request.
- `CENSOR_OPTIONS_FILE`: censor options for requests without the header.
- `VIDEO_CENSOR_PORT` (in `.env`): the host port (default 2383).

Tests: `cd video && python3 -m unittest discover -s tests` (needs numpy and opencv-python-headless,
plus ffmpeg for the pipeline tests), or inside the image with
`docker run --rm --entrypoint python3 video-censor -m unittest discover -s tests`.

## Network access

The server has no authentication, so by default it only accepts connections from the computer it
runs on, and of the requests that come from a browser it only accepts those from extensions and
from its own pages (status page, `/swagger`), not from other web pages. Before v0.3.0 it listened on every network interface and read local
files named in a request, so anyone on the network, or any web page open in the browser, could have
it return image files from the disk.

To use the server from other devices on the same network (Beta Protection on another computer, or
in a phone browser that runs extensions, with `http://<this computer's IP>:2382` as Backend Host),
set in `config.yml`:

```yaml
Server:
  ListenAddress: "*"   # every network interface; anyone who can reach port 2382 can use the server
```

Only do that on a network you trust, and allow port 2382 in the computer's firewall. Other
`Server:` settings:

- `Port` (2382).
- `AllowedOrigins`: web pages allowed to call the server, such as `["https://my-gallery.example"]`.
- `AllowedExtensions`: ids of the browser extensions allowed to connect (from the browser's
  extensions page). Empty, the default, allows every extension installed in the browser.
- `AllowedHosts`: names the server is reached by. Needed only for a name with dots that doesn't end
  in `.local`, such as `censor.example.org`; localhost, IP addresses, plain machine names and Docker
  service names always work. Requests addressed to other names get 403, which keeps web pages from
  reaching the server under a name of their own.
- `EnableDiscovery`: announce the server over mDNS.

The server downloads the image URLs it is given, so its clients can make it request addresses on
your network. That is limited to clients that may use the server at all.

## Sticker packs

The original sticker packs can no longer be downloaded, and none are included here. A pack is a
folder of images per category:

- **Folder:** `stickers/<Category>/*.png` in the server's folder. Images in subfolders of a
  category count towards it.
- **Zip:** a `*.betapkg` (or `*-stickers.zip`) with the category folders at its root, in the
  server's folder or in `stickers/`.
- **Elsewhere:** `Stickers: LocalStores: [<folder or zip>]`, or `Stickers: Paths: {Category:
  [<folders>]}` in `config.yml`.

Transparent PNGs work best; anything ImageSharp reads (PNG, JPEG, WebP, GIF, BMP, TIFF) is
accepted. A sticker only goes on a censored area whose aspect ratio is within 25% of its own
(`Stickers: RatioMargin`); areas with no fitting sticker only get the blur, so include several
shapes: square, 3:2 and 2:1 wide, 2:3 and 1:2 tall, and around 4:1 for eyes. Restart the server
after changing a pack; the categories then appear in Beta Protection's sticker settings.

## Run as a service

These instructions haven't been tested yet.

- **Windows:** `sc.exe create BetaCensoring binPath= "C:\path\to\BetaCensor.Server.exe" start= auto`
  in an administrator prompt, then `sc.exe start BetaCensoring`. The server reads `config.yml`
  from its own folder. Put the model file there too: Windows stops services that take more than
  30 s to start, which a model download can.
- **Linux (systemd):** a unit with `Type=notify`, `ExecStart=/path/to/BetaCensor.Server` and
  `WorkingDirectory=/path/to` (the folder with `config.yml` and `stickers/`), and a `User=` of your
  choice.

## API

Besides the SignalR hub at `/live` that Beta Protection uses, there is a REST API; `/swagger`
lists every endpoint.

```bash
curl -X POST http://localhost:2382/censoring/censorImage -H "Content-Type: application/json" \
  -d '{"imageUrl": "https://example.com/photo.jpg",
       "censorOptions": {"EXPOSED_BREAST_F": {"censorType": "pixelate", "level": 10}}}' -o censored.jpg
```

- `imageUrl` (http or https) or `imageDataUrl` (a `data:` URI) says which image; local files
  aren't accepted. The `Content-Type: application/json` header is required.
- Censor types: `blur`, `pixelate`, `blackbars`, `sticker:<category>`, `caption`, and `none`;
  `level` runs from 1 to 10.
- Classes (NudeNet v3, with the v2 names): `EXPOSED_BREAST_F`, `EXPOSED_GENITALIA_F`,
  `EXPOSED_GENITALIA_M`, `EXPOSED_BUTTOCKS`, `EXPOSED_ANUS`, `EXPOSED_BELLY`, `EXPOSED_FEET`,
  `EXPOSED_ARMPITS`, `EXPOSED_BREAST_M`, `COVERED_BREAST_F`, `COVERED_GENITALIA_F`,
  `COVERED_BUTTOCKS`, `COVERED_BELLY`, `COVERED_FEET`, `COVERED_ARMPITS`, `COVERED_ANUS`, `FACE_F`,
  `FACE_M`. There are none for other body parts, such as legs.
- `POST /censoring/detect` and `/censoring/detectBatch` return the matches without censoring (see
  "What changed").
- Errors: 400 for an unsupported image source, 403 for a request from a web page, 413 for an
  image over 250 million pixels, 422 when it couldn't be read, 502 when it couldn't be downloaded.

## Run without Docker

Needs the .NET 10 SDK.

```bash
dotnet publish beta-censoring/src/BetaCensor.Server/BetaCensor.Server.csproj -c Release -o out
BCS_ModelPath=models/640m.onnx dotnet out/BetaCensor.Server.dll
```

The status page at `http://localhost:2382` is empty unless its front end is built first
(`npm ci && npm run build` in `beta-censoring/src/BetaCensor.Web.Status/ClientApp`, Node 16);
the Docker build does this. The censoring API works either way.

## Notes

- The v3 thresholds were picked from a small non-explicit sample; tune `MatchOptions` if it
  censors too much or too little.
- GPU acceleration: CUDA on Linux with the GPU build (see above); DirectML on Windows is untested.

## Credits and licence

- [censor-core](https://github.com/silveredgold/censor-core) and
  [beta-censoring](https://github.com/silveredgold/beta-censoring) by silveredgold, GPL-3.0.
  Their commit histories are included unchanged apart from the move into subdirectories.
- [NudeNet](https://github.com/notAI-tech/NudeNet) by notAI-tech (AGPL-3.0) provides the detection
  models, which are downloaded separately.
- [ImageSharp](https://github.com/SixLabors/ImageSharp) and ImageSharp.Drawing by Six Labors are used under
  the Apache License 2.0, as granted by the Six Labors Split License for open source software.

This project is licensed under the GNU General Public License v3.0, like the originals (see
`censor-core/LICENSE` and `beta-censoring/LICENSE`). `NOTICE` describes what has been modified.
