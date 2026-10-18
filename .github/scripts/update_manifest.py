#!/usr/bin/env python3
"""Add (or replace) a version entry in a Jellyfin plugin repository manifest.json."""
import argparse
import datetime
import json

parser = argparse.ArgumentParser()
parser.add_argument("manifest")
parser.add_argument("--guid", required=True)
parser.add_argument("--version", required=True)
parser.add_argument("--target-abi", required=True)
parser.add_argument("--source-url", required=True)
parser.add_argument("--checksum", required=True)
parser.add_argument("--changelog", default="")
args = parser.parse_args()

with open(args.manifest, encoding="utf-8") as f:
    manifest = json.load(f)

plugin = next((p for p in manifest if p["guid"].lower() == args.guid.lower()), None)
if plugin is None:
    raise SystemExit(f"plugin {args.guid} not found in {args.manifest}")

entry = {
    "version": args.version,
    "changelog": args.changelog,
    "targetAbi": args.target_abi,
    "sourceUrl": args.source_url,
    "checksum": args.checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

versions = [v for v in plugin.get("versions", []) if v["version"] != args.version]
versions.insert(0, entry)
versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
plugin["versions"] = versions

with open(args.manifest, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
