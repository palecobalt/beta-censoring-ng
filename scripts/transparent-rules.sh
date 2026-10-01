#!/bin/sh
# Prints the firewall rules that send chosen devices' web traffic into the proxy's transparent listener
# (docker-compose.transparent.yml). It only prints them: read them, then run them as root or add them to your
# firewall's own configuration so they survive a reboot.
#
#   scripts/transparent-rules.sh <interface> <device address>...        rules to add
#   scripts/transparent-rules.sh --remove <interface> <device address>...   the same rules, removed
#
# <interface> is where the devices' traffic arrives (wg0, tailscale0, ...); the addresses are the devices' addresses
# on that network, IPv4 or IPv6. Only the listed devices are intercepted. TRANSPARENT_PORT changes the port (8081).
set -eu

action=-A
# the two DROP rules go to the top of their chains: a VPN's own "accept everything from this interface" rule, or a
# firewall's accept rules, would otherwise be matched first and the DROP never reached
first="-I"
top=" 1"
if [ "${1:-}" = "--remove" ]; then
    action=-D
    first=-D
    top=
    shift
fi
if [ $# -lt 2 ]; then
    sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
fi
iface=$1
shift
port=${TRANSPARENT_PORT:-8081}
mark=0x$port

for address in "$@"; do
    case $address in
        *:*) ipt=ip6tables ;;
        *) ipt=iptables ;;
    esac
    echo "# $address on $iface"
    # tag the device's web connections, so the last rule can tell them from someone dialling the port directly
    echo "$ipt -t mangle $action PREROUTING -i $iface -s $address -p tcp -m multiport --dports 80,443 -j MARK --set-mark $mark"
    # hand them to the proxy
    echo "$ipt -t nat $action PREROUTING -i $iface -s $address -p tcp -m multiport --dports 80,443 -j REDIRECT --to-port $port"
    # no HTTP/3 (QUIC): it runs over UDP, which the proxy doesn't see, so browsers must fall back to TCP
    echo "$ipt $first FORWARD$top -i $iface -s $address -p udp --dport 443 -j DROP"
done
echo "# the transparent port only takes redirected connections, so nobody can use it as an open proxy"
echo "iptables $first INPUT$top -p tcp --dport $port -m mark ! --mark $mark -j DROP"
echo "ip6tables $first INPUT$top -p tcp --dport $port -m mark ! --mark $mark -j DROP"
