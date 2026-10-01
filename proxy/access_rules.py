"""Which connections the proxy leaves alone: hosts that are never intercepted, and hosts whose clients reject the
proxy's certificate (apps with a pinned certificate). No mitmproxy imports, so it can be tested on its own."""
import os

# Android decides whether a network has internet access with plain-HTTP requests for .../generate_204 and an HTTPS
# request to this host. On a Wi-Fi network with a proxy they go through the proxy, sent by a system component that
# has no proxy password to give and doesn't trust certificates the user installed. When they fail, the phone shows
# the Wi-Fi as "no internet" and prefers mobile data, which doesn't go through the proxy at all. So the proxy
# answers the HTTP checks itself and lets the HTTPS one through untouched, both without a password.
CONNECTIVITY_CHECK_HOST = "www.google.com"
CONNECTIVITY_CHECK_PATHS = ("/generate_204", "/gen_204")

# sign-in hosts of large identity providers: nothing to censor there, and logins tend to break behind any intercepting
# proxy; and the connectivity check's host (the images of Google's pages come from other hosts and stay censored).
# Extended with CENSOR_PASSTHROUGH_HOSTS; "none" in that list drops these defaults.
DEFAULT_PASSTHROUGH = (
    CONNECTIVITY_CHECK_HOST,
    "accounts.google.com",
    "appleid.apple.com",
    "idmsa.apple.com",
    "login.live.com",
    "login.microsoftonline.com",
)


def parse_hosts(text: str | None) -> list[str]:
    """Host names from a comma, space or line separated list; "*.example.com" means the same as "example.com"."""
    hosts = []
    for item in (text or "").replace(",", " ").split():
        item = item.strip().lower().removeprefix("*.").strip(".")
        if item and item not in hosts:
            hosts.append(item)
    return hosts


# A client that hangs up during the TLS handshake without saying why: browsers do that with connections they opened
# in advance and then didn't need, so it only counts as rejecting the certificate after this many in a row.
UNEXPLAINED_FAILURES = 3
# client and host pairs remembered at most; the oldest are forgotten first, and found again if they still fail
MAX_REMEMBERED = 5000


def is_rejection(error: str | None) -> bool:
    """True when the client said it doesn't accept the certificate (a TLS alert), going by mitmproxy's error text."""
    error = (error or "").lower()
    return any(alert in error for alert in ("unknown ca", "bad certificate", "certificate unknown"))


def is_connectivity_check(method: str, scheme: str, path: str) -> bool:
    """A plain-HTTP connectivity check, which the proxy can answer itself: nothing is fetched for it."""
    return method.upper() in ("GET", "HEAD") and scheme == "http" and path.split("?")[0] in CONNECTIVITY_CHECK_PATHS


def is_connectivity_check_tunnel(host: str | None, port: int, passthrough: list[str]) -> bool:
    """A tunnel to the HTTPS connectivity check's host, as long as that host is passed through untouched."""
    return bool(host) and host.lower().strip(".") == CONNECTIVITY_CHECK_HOST and port == 443 and matches(host, passthrough)


def passthrough_hosts(configured: str | None) -> list[str]:
    hosts = parse_hosts(configured)
    if "none" in hosts:
        return [h for h in hosts if h != "none"]
    return list(DEFAULT_PASSTHROUGH) + [h for h in hosts if h not in DEFAULT_PASSTHROUGH]


def matches(host: str | None, patterns: list[str]) -> bool:
    """True for a listed host and its subdomains: "example.com" covers "example.com" and "login.example.com"."""
    if not host:
        return False
    host = host.lower().strip(".")
    return any(host == p or host.endswith("." + p) for p in patterns)


class PinnedHosts:
    """Remembers where a client rejected the proxy's certificate, per client address and host.

    That happens when an app only trusts its own (pinned) certificate, and also when the proxy's CA isn't installed
    on the device at all. With policy "block" such connections keep failing, so nothing uncensored gets through.
    With "pass" the host is passed through untouched (and uncensored) for that client from the second attempt on;
    other devices are still censored. A browser on the same device can't be told apart from the app.

    A client that just hangs up during the handshake only counts after UNEXPLAINED_FAILURES times in a row with no
    successful handshake in between: browsers drop unused connections that way all the time.
    """

    def __init__(self, policy: str = "block", path: str | None = None):
        self.policy = "pass" if policy.strip().lower() == "pass" else "block"
        self._path = path
        # a dict for its insertion order: the oldest entries are dropped when there are too many
        self._seen: dict[tuple[str, str], None] = {}
        self._unexplained: dict[tuple[str, str], int] = {}
        if self.policy == "pass" and path and os.path.exists(path):
            try:
                with open(path) as f:
                    for line in f:
                        parts = line.split()
                        if len(parts) == 2:
                            self._seen[(parts[0], parts[1])] = None
            except OSError:
                pass

    @staticmethod
    def _key(client: str | None, host: str | None):
        return (client or "any", host.lower().strip(".")) if host else None

    def record_failure(self, client: str | None, host: str | None, rejected: bool = True) -> bool:
        """Notes a failed handshake of this client for this host: rejected with a TLS alert, or just dropped.
        True when that makes the host count as rejecting the certificate, which happens once, so it's logged once."""
        key = self._key(client, host)
        if key is None or key in self._seen:
            return False
        if not rejected:
            self._unexplained[key] = self._unexplained.get(key, 0) + 1
            self._trim(self._unexplained)
            if self._unexplained.get(key, 0) < UNEXPLAINED_FAILURES:
                return False
        self._unexplained.pop(key, None)
        self._seen[key] = None
        self._trim(self._seen)
        if self.policy == "pass" and self._path:
            try:
                with open(self._path, "w") as f:
                    f.write("".join(f"{c} {h}\n" for c, h in sorted(self._seen)))
            except OSError:
                pass
        return True

    @staticmethod
    def _trim(entries: dict) -> None:
        while len(entries) > MAX_REMEMBERED:
            del entries[next(iter(entries))]

    def record_success(self, client: str | None, host: str | None) -> None:
        """A handshake that worked: earlier dropped connections for this host were not about the certificate."""
        self._unexplained.pop(self._key(client, host), None)

    def should_pass(self, client: str | None, host: str | None) -> bool:
        return self.policy == "pass" and self._key(client, host) in self._seen
