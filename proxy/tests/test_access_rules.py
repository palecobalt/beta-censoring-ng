import os
import tempfile

import access_rules
from access_rules import PinnedHosts, matches, parse_hosts, passthrough_hosts


def test_parses_host_lists():
    assert parse_hosts(" Example.com, *.bank.example\nlogin.example.org. example.com ") == [
        "example.com", "bank.example", "login.example.org"]
    assert parse_hosts(None) == []


def test_matches_hosts_and_subdomains_only():
    patterns = ["example.com"]
    assert matches("example.com", patterns)
    assert matches("Login.Example.com.", patterns)
    assert not matches("notexample.com", patterns)
    assert not matches("example.com.evil.test", patterns)
    assert not matches(None, patterns)


def test_connectivity_checks_are_recognised():
    assert access_rules.is_connectivity_check("GET", "http", "/generate_204")
    assert access_rules.is_connectivity_check("GET", "http", "/gen_204?x=1")
    assert not access_rules.is_connectivity_check("GET", "https", "/generate_204")
    assert not access_rules.is_connectivity_check("POST", "http", "/generate_204")
    assert not access_rules.is_connectivity_check("GET", "http", "/generate_204/more")
    assert not access_rules.is_connectivity_check("GET", "http", "/")


def test_only_the_check_host_gets_a_tunnel_without_a_password():
    defaults = passthrough_hosts(None)
    assert access_rules.is_connectivity_check_tunnel("www.google.com", 443, defaults)
    assert not access_rules.is_connectivity_check_tunnel("www.google.com", 80, defaults)
    assert not access_rules.is_connectivity_check_tunnel("accounts.google.com", 443, defaults)
    assert not access_rules.is_connectivity_check_tunnel("evil.example", 443, defaults + ["evil.example"])
    assert not access_rules.is_connectivity_check_tunnel(None, 443, defaults)
    # not when the host is intercepted: its requests would need the password anyway
    assert not access_rules.is_connectivity_check_tunnel("www.google.com", 443, passthrough_hosts("none"))


def test_defaults_can_be_extended_or_dropped():
    hosts = passthrough_hosts("mybank.example")
    assert "accounts.google.com" in hosts and "mybank.example" in hosts
    assert passthrough_hosts("none, mybank.example") == ["mybank.example"]
    assert passthrough_hosts(None) == list(access_rules.DEFAULT_PASSTHROUGH)


def test_block_policy_never_passes_but_reports_once():
    pinned = PinnedHosts("block")
    assert pinned.record_failure("10.0.0.5", "api.app.example")
    assert not pinned.record_failure("10.0.0.5", "api.app.example")
    assert not pinned.should_pass("10.0.0.5", "api.app.example")


def test_pass_policy_passes_after_the_first_failure_for_that_client_only():
    pinned = PinnedHosts("pass")
    assert not pinned.should_pass("10.0.0.5", "api.app.example")
    pinned.record_failure("10.0.0.5", "API.app.example")
    assert pinned.should_pass("10.0.0.5", "api.app.example")
    assert not pinned.should_pass("10.0.0.5", "other.example")
    # another device is still intercepted
    assert not pinned.should_pass("10.0.0.6", "api.app.example")


def test_pass_policy_remembers_hosts_across_restarts():
    with tempfile.TemporaryDirectory() as folder:
        path = os.path.join(folder, "pinned-hosts.txt")
        PinnedHosts("pass", path).record_failure("10.0.0.5", "api.app.example")
        assert PinnedHosts("pass", path).should_pass("10.0.0.5", "api.app.example")
        # switching back to block ignores what was learned
        assert not PinnedHosts("block", path).should_pass("10.0.0.5", "api.app.example")


def test_a_dropped_handshake_only_counts_when_it_keeps_happening():
    pinned = PinnedHosts("pass")
    # a browser closing connections it opened in advance
    assert not pinned.record_failure("10.0.0.5", "images.example", rejected=False)
    assert not pinned.record_failure("10.0.0.5", "images.example", rejected=False)
    assert not pinned.should_pass("10.0.0.5", "images.example")
    # a handshake that works in between starts the count again
    pinned.record_success("10.0.0.5", "images.example")
    assert not pinned.record_failure("10.0.0.5", "images.example", rejected=False)
    assert not pinned.record_failure("10.0.0.5", "images.example", rejected=False)
    assert not pinned.should_pass("10.0.0.5", "images.example")
    # an app that hangs up every time
    assert pinned.record_failure("10.0.0.5", "images.example", rejected=False)
    assert pinned.should_pass("10.0.0.5", "images.example")


def test_recognises_a_rejected_certificate_in_mitmproxys_error():
    assert access_rules.is_rejection("The client does not trust the proxy's certificate for example.com "
                                     "(OpenSSL Error([('SSL routines', '', 'tls alert certificate unknown')]))")
    assert access_rules.is_rejection("OpenSSL Error([('SSL routines', '', 'tlsv1 alert unknown ca')])")
    assert access_rules.is_rejection("OpenSSL Error([('SSL routines', '', 'sslv3 alert bad certificate')])")
    assert not access_rules.is_rejection("The client disconnected during the handshake. If this happens consistently "
                                         "for example.com, this may indicate that the client does not trust the "
                                         "proxy's certificate.")
    assert not access_rules.is_rejection(None)


def test_forgets_the_oldest_entries_beyond_the_limit(monkeypatch):
    monkeypatch.setattr(access_rules, "MAX_REMEMBERED", 3)
    pinned = PinnedHosts("pass")
    for n in range(5):
        pinned.record_failure("10.0.0.5", f"host{n}.example")
        pinned.record_failure("10.0.0.5", f"dropped{n}.example", rejected=False)
    assert len(pinned._seen) == 3 and len(pinned._unexplained) == 3
    assert not pinned.should_pass("10.0.0.5", "host0.example")
    assert pinned.should_pass("10.0.0.5", "host4.example")
    # a forgotten host is found again the next time it fails
    assert pinned.record_failure("10.0.0.5", "host0.example")


def test_unknown_policy_means_block():
    assert PinnedHosts("allow-everything").policy == "block"


def test_ignores_connections_without_a_host():
    pinned = PinnedHosts("pass")
    assert not pinned.record_failure("10.0.0.5", None)
    assert not pinned.should_pass("10.0.0.5", None)
