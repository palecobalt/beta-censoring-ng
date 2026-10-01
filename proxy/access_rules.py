"""Which connections the proxy leaves alone: hosts that are never intercepted, and hosts whose clients reject the
proxy's certificate (apps with a pinned certificate). No mitmproxy imports, so it can be tested on its own."""
import os

# sign-in hosts of large identity providers: nothing to censor there, and logins tend to break behind any intercepting
# proxy. Extended with CENSOR_PASSTHROUGH_HOSTS; "none" in that list drops these defaults.
DEFAULT_PASSTHROUGH = (
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
    """

    def __init__(self, policy: str = "block", path: str | None = None):
        self.policy = "pass" if policy.strip().lower() == "pass" else "block"
        self._path = path
        self._seen: set[tuple[str, str]] = set()
        if self.policy == "pass" and path and os.path.exists(path):
            try:
                with open(path) as f:
                    for line in f:
                        parts = line.split()
                        if len(parts) == 2:
                            self._seen.add((parts[0], parts[1]))
            except OSError:
                pass

    @staticmethod
    def _key(client: str | None, host: str | None):
        return (client or "any", host.lower().strip(".")) if host else None

    def record_failure(self, client: str | None, host: str | None) -> bool:
        """Notes that this client rejected the certificate for this host; True the first time, so it's logged once."""
        key = self._key(client, host)
        if key is None or key in self._seen:
            return False
        self._seen.add(key)
        if self.policy == "pass" and self._path:
            try:
                with open(self._path, "w") as f:
                    f.write("".join(f"{c} {h}\n" for c, h in sorted(self._seen)))
            except OSError:
                pass
        return True

    def should_pass(self, client: str | None, host: str | None) -> bool:
        return self.policy == "pass" and self._key(client, host) in self._seen
