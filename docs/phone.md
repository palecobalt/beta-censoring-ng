# Censoring on a phone, through your own computer

A phone can't run the detection model at a useful speed, and Beta Protection doesn't run in most phone browsers. The
censoring proxy solves both: the phone's web traffic goes through your computer, which censors the images and sends
them on. Nothing leaves your own devices, and there is no account.

Two ways to set it up:

| | Simple: the phone's proxy setting | Anywhere: your own VPN with a transparent proxy |
|---|---|---|
| Works | on your home Wi-Fi | anywhere, also on mobile data |
| Covers | browsers on that Wi-Fi network | browsers on any network |
| Phone setup | proxy address in the Wi-Fi settings, the certificate | a VPN app, the certificate |
| Computer setup | `docker compose up -d` | the same, plus a VPN and four firewall rules; Linux only |

Both need the proxy's certificate on the phone; read "What the certificate means" first. Both censor browsers
(Chrome and its relatives, Firefox, Safari); other apps are a different matter, see "Apps".

The Android steps were tested on Android 15 with Chrome and Firefox. Menu names differ between versions and
manufacturers. The iPhone steps follow Apple's menus and have not been tested. The computer side was tested on Linux.

## What the certificate means

Websites are encrypted, so to censor images the proxy has to decrypt them. It does that with its own certificate
authority (CA), created on your computer the first time the proxy starts and kept in the `mitmproxy-ca` Docker volume.
A phone that trusts this CA lets the proxy read everything that phone sends through it, passwords included.

- Only install it on devices you want censored, and only from your own proxy.
- Anyone who gets the CA's key could impersonate websites to those devices. Don't copy the volume around, and leave
  it out of backups you don't control.
- Keep banks and similar sites out of the proxy: list them in `CENSOR_PASSTHROUGH_HOSTS` (see "Settings").
- To stop: remove the certificate from the phone (last section). To invalidate it everywhere: `docker compose down`,
  `docker volume rm <project>_mitmproxy-ca`, start again; a new CA is created.

## Simple: the phone's proxy setting

On the computer, next to `docker-compose.yml`, create `.env`:

```
PROXY_AUTH=phone:choose-a-password
```

then `docker compose up -d`. The proxy listens on port 8080 on every network interface of the computer, and the
password keeps other people on your network from using it. Allow port 8080 in the computer's firewall.

On an Android phone:

1. Settings → Network & internet → Internet → your Wi-Fi network → edit (the pencil) → Advanced options → Proxy:
   Manual. Host: the computer's address on your network; port: 8080.
2. Open `http://mitm.it` in Chrome and enter the proxy's user name and password when asked. The page only shows the
   certificates when the request went through the proxy. Download the Android one; Chrome warns that the file can't
   be downloaded securely (keep it), and Android then says it has to be installed in Settings.
3. Settings → Security & privacy → More security & privacy → Encryption & credentials → Install a certificate → CA
   certificate → Install anyway, and pick the downloaded file. Some phones want a screen lock set first.
4. Browse. Images are censored as pages load, so pages are slower.

Firefox for Android follows the same Wi-Fi proxy setting and asks for the password too, but it ignores certificates
installed on the phone until "Use third party CA certificates" is turned on: Settings → About Firefox, tap the logo
five times, go back, Secret Settings.

On an iPhone or iPad:

1. Settings → Wi-Fi → the ⓘ next to your network → Configure Proxy → Manual: the computer's address, port 8080,
   Authentication on, with the user name and password.
2. Open `http://mitm.it` in Safari, download the iOS profile, then Settings → General → VPN & Device Management →
   install it.
3. Settings → General → About → Certificate Trust Settings → turn on full trust for mitmproxy.

Limits of this path: the setting belongs to the Wi-Fi network, so it applies to the whole phone there and to nothing
on mobile data. Apps that use the setting can't ask for the proxy password and so have no connection on that Wi-Fi
(without a password they meet the certificate problem described under "Apps"); apps that ignore the setting connect
directly, uncensored.

Android checks every network for internet access, through the proxy when one is set. The proxy answers that check
without the password and without decrypting it; otherwise the phone would show the Wi-Fi as having no internet and
move to mobile data, past the proxy.

## Anywhere: your own VPN with a transparent proxy

Here the phone sends all its traffic to your computer through a VPN you run yourself (WireGuard, or Tailscale with the
computer as exit node), and the computer hands the web part of it to the proxy. The phone needs no proxy setting, so
this also works with Firefox for Android, with apps, and away from home.

On the computer (Linux):

1. Set up the VPN so the phone can use the computer as its gateway: for WireGuard, a peer with `AllowedIPs = 0.0.0.0/0`
   on the phone and forwarding plus NAT on the computer; for Tailscale, `tailscale up --advertise-exit-node` and
   approve the exit node in the admin console. Check the phone can browse through it before going on.
2. Start the stack in transparent mode:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.transparent.yml up -d
   ```

3. Print the firewall rules for the phone's address on the VPN, read them, and run them as root:

   ```bash
   scripts/transparent-rules.sh wg0 10.8.0.2          # interface, then one or more device addresses
   ```

   Per device, the rules send its web connections (TCP 80 and 443) to the proxy and drop HTTP/3 (UDP 443), which
   the proxy can't see; one more rule keeps everyone else off the transparent port. The two rules that drop packets
   are put at the top of their chains, ahead of the VPN's own rule that accepts the phone's traffic. Only the listed devices are
   intercepted: another device using the same VPN is left alone. The rules are gone after a reboot unless you add
   them to your firewall's own configuration. `scripts/transparent-rules.sh --remove ...` prints the commands that
   remove them. If the phone has an IPv6 address on the VPN, list it too, or its IPv6 traffic bypasses the proxy.

On the phone:

1. Connect the VPN and route everything through the computer (WireGuard: activate the tunnel; Tailscale: choose the
   computer as exit node).
2. Open `http://mitm.it` and install the certificate as in the simple path (steps 2 and 3 there; nothing asks for a
   password here). Firefox for Android needs its "Use third party CA certificates" setting, as described there.

**Censoring only part of the phone (Android):** add a second user (Settings → System → Multiple users), install the VPN
app and the certificate in that user only, and list that user's VPN address in the rules. The main user stays
untouched. Android's Private Space doesn't work for this: its VPN never connects.

## Apps

Browsers accept a certificate the user installed. Almost no other Android app does: unless its developer allowed it,
an app only trusts the certificates that came with the phone, and some apps trust nothing but their own (a pinned
certificate). Either way the app rejects the proxy's certificate, so no proxy can censor it. This includes ordinary
apps without any special protection, and Google's own services on the phone. What the proxy does with them is set by
`PINNED_POLICY`:

- `block` (default): their connections fail, and the proxy's log names each host once. On the device or profile that
  goes through the proxy, only browsers work. Nothing gets through uncensored.
- `pass`: a host an app was refused on is passed through untouched for that device from the next attempt on, so apps
  work, uncensored. The proxy can't tell the app from a browser on the same device: once an app has unlocked a host,
  the browser's pages and images from that host are uncensored too. A browser that lacks the certificate (Firefox
  before its setting is turned on) unlocks hosts the same way. The list is kept in the CA volume
  (`pinned-hosts.txt`); delete the file and restart the proxy to start again.

Using a site in the browser instead of its app is what gets it censored.

## What doesn't work

- **Logging in to some sites** fails behind the proxy. Add the site's sign-in host to `CENSOR_PASSTHROUGH_HOSTS`; if
  the site signs in on its main host, add that one: its images usually come from a different host and stay censored.
- **Video** follows the proxy's video settings: short clips are censored, streams are blocked or passed (see the
  README).
- **A device without the certificate** behaves like those apps everywhere: every HTTPS connection fails.

## Settings

In `.env` next to `docker-compose.yml`:

| Setting | Meaning |
|---|---|
| `PROXY_AUTH=user:password` | clients must log in to the proxy (simple path; a transparent proxy can't ask) |
| `PROXY_BIND=127.0.0.1` | publish the proxy on one address instead of every interface |
| `PROXY_PORT`, `TRANSPARENT_PORT` | ports, 8080 and 8081 by default |
| `CENSOR_PASSTHROUGH_HOSTS=mybank.example,login.example.org` | hosts, with their subdomains, that are never intercepted; the sign-in hosts of Google, Apple and Microsoft and `www.google.com` (Android's internet check; the images on Google's pages come from other hosts) are built in |
| `PINNED_POLICY=block` or `pass` | what happens to apps that reject the certificate; see "Apps" |

On a computer without a GPU, the server's speed decides how slow pages feel; see "Models" in the README.

Checking it works: `docker logs censor-proxy` shows warnings and rejected certificates; a censored image answers with
the header `x-censored: 1`; `http://mitm.it` only shows the certificate page when the request really went through the
proxy.

## Undo

- Phone: remove the certificate (Android: Settings → Security → Encryption & credentials → User credentials; iOS:
  remove the profile under VPN & Device Management), then remove the Wi-Fi proxy setting or disconnect the VPN.
- Computer: run the commands printed by `scripts/transparent-rules.sh --remove ...`, and `docker compose down`.
