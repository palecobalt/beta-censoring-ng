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


def test_unknown_policy_means_block():
    assert PinnedHosts("allow-everything").policy == "block"


def test_ignores_connections_without_a_host():
    pinned = PinnedHosts("pass")
    assert not pinned.record_failure("10.0.0.5", None)
    assert not pinned.should_pass("10.0.0.5", None)
