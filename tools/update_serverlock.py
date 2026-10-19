#!/usr/bin/env python3
"""Erzeugt RetracBoost/ServerLockData.cs aus den offiziellen AWS-IP-Bereichen.

Gesperrt werden ALLE AWS-Bereiche (alle Dienste, IPv4 + IPv6) in Europa ausser Frankfurt (eu-central-1):
London, Irland, Paris, Stockholm, Mailand, Spanien, Zuerich und die European Sovereign Cloud.
Bereiche, die sich mit Frankfurt ueberschneiden, werden nie gesperrt.

Aufruf:
    python tools/update_serverlock.py                    # laedt ip-ranges.json frisch von AWS
    python tools/update_serverlock.py --input aws-ranges.json
"""
import argparse
import ipaddress
import json
import logging
import sys
import urllib.request
from pathlib import Path

AWS_URL = "https://ip-ranges.amazonaws.com/ip-ranges.json"
FRANKFURT = "eu-central-1"
ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = ROOT / "RetracBoost" / "ServerLockData.cs"

log = logging.getLogger("serverlock")


def load(path, url, timeout):
    if path:
        log.info("lese %s", path)
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    log.info("lade %s", url)
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def is_europe(region):
    return region.startswith("eu-") or region.startswith("eusc-")


def collect(data):
    fra, blocked, regions = [], [], set()
    for key, field in (("prefixes", "ip_prefix"), ("ipv6_prefixes", "ipv6_prefix")):
        for p in data.get(key, []):
            region = p.get("region", "")
            net = ipaddress.ip_network(p[field], strict=False)
            if region == FRANKFURT:
                fra.append(net)
            elif is_europe(region):
                blocked.append(net)
                regions.add(region)
    return fra, blocked, regions


def collapse(nets):
    v4 = list(ipaddress.collapse_addresses(n for n in nets if n.version == 4))
    v6 = list(ipaddress.collapse_addresses(n for n in nets if n.version == 6))
    return v4 + v6


def without_overlap(blocked, keep):
    out = []
    for b in blocked:
        if any(b.version == k.version and b.overlaps(k) for k in keep):
            log.debug("ueberschneidet Frankfurt, wird nicht gesperrt: %s", b)
            continue
        out.append(b)
    return out


def cs_array(name, nets, indent="        "):
    lines = [f"{indent}public static readonly string[] {name} = new[]", f"{indent}{{"]
    for i, n in enumerate(nets):
        comma = "," if i < len(nets) - 1 else ""
        lines.append(f'{indent}    "{n}"{comma}')
    lines.append(f"{indent}}};")
    return "\n".join(lines)


def render(stamp, fra, blocked, regions):
    reg = ", ".join(sorted(regions))
    return f"""namespace RetracBoost
{{
    /// <summary>
    /// Automatisch erzeugt von tools/update_serverlock.py - nicht von Hand bearbeiten.
    /// Quelle: {AWS_URL} (Stand {stamp}).
    /// </summary>
    static partial class ServerLockData
    {{
        public const string Stamp = "{stamp}";

        // {len(blocked)} Bereiche aller AWS-Dienste (IPv4 + IPv6) in Europa ausser Frankfurt:
        // {reg}
{cs_array("BlockedCidrs", blocked)}

        // {len(fra)} Bereiche von Frankfurt (eu-central-1) - zur Erkennung des Match-Servers.
{cs_array("FrankfurtCidrs", fra)}
    }}
}}
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--input", help="lokale ip-ranges.json statt Download")
    ap.add_argument("--url", default=AWS_URL)
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--timeout", type=int, default=30)
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(levelname)s %(message)s")

    try:
        data = load(args.input, args.url, args.timeout)
    except (OSError, ValueError) as e:
        log.error("AWS-Daten nicht lesbar: %s", e)
        return 1

    fra, blocked, regions = collect(data)
    if not fra or not blocked:
        log.error("unerwartete Daten: Frankfurt=%d, gesperrt=%d", len(fra), len(blocked))
        return 1

    fra = collapse(fra)
    blocked = without_overlap(collapse(blocked), fra)
    stamp = data.get("createDate", "unbekannt")

    out = Path(args.out)
    out.write_text(render(stamp, fra, blocked, regions).replace("\n", "\r\n"), encoding="utf-8")
    log.info("%s geschrieben: %d gesperrte Bereiche, %d Frankfurt-Bereiche, Regionen: %s",
             out, len(blocked), len(fra), ", ".join(sorted(regions)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
