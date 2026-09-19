#!/usr/bin/env python3
import argparse
import json
import pathlib
import sys
import urllib.error
import urllib.request

ANDROID_CLIENT_ID = "kd1unb4b3q4t58fwlpcbzcbnm76a8fp"
ANDROID_UA = "Dalvik/2.1.0 (Linux; U; Android 15; SM-G977N Build/BP1A.250505.005)"
GQL_URL = "https://gql.twitch.tv/gql"

FALLBACK_HASHES = {
    "ViewerDropsDashboard": "5a4da2ab3d5b47c9f9ce864e727b2cb346af1e3ea8b897fe8f704a97ff017619",
    "DropCampaignDetails": "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1",
}


def get_ci(obj, key, default=None):
    if isinstance(obj, dict):
        if key in obj:
            return obj[key]
        for k, v in obj.items():
            if k.lower() == key.lower():
                return v
    return default


def find_hashes(collection_path):
    hashes = dict(FALLBACK_HASHES)
    p = pathlib.Path(collection_path) if collection_path else None
    if p and p.is_file():
        try:
            data = json.loads(p.read_text())
            for item in data.get("item", []):
                name = item.get("name")
                raw = item.get("request", {}).get("body", {}).get("raw")
                if not name or not raw:
                    continue
                body = json.loads(raw)
                h = body.get("extensions", {}).get("persistedQuery", {}).get("sha256Hash")
                if h:
                    hashes[name] = h
        except Exception as exc:
            print(f"warn: could not read collection ({exc}); using fallback hashes", file=sys.stderr)
    return hashes


def gql(operation, variables, token, hashes):
    payload = {
        "operationName": operation,
        "variables": variables,
        "extensions": {"persistedQuery": {"version": 1, "sha256Hash": hashes[operation]}},
    }
    req = urllib.request.Request(
        GQL_URL,
        data=json.dumps(payload).encode(),
        headers={
            "Client-Id": ANDROID_CLIENT_ID,
            "Authorization": f"OAuth {token}",
            "Content-Type": "application/json",
            "Origin": "https://www.twitch.tv",
            "Referer": "https://www.twitch.tv",
            "User-Agent": ANDROID_UA,
        },
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode())


def load_users(config_path):
    data = json.loads(pathlib.Path(config_path).read_text())
    twitch = data.get("TwitchSettings") or {}
    return [u for u in (twitch.get("TwitchUsers") or []) if u.get("Enabled")]


def reward_rows(campaign):
    rows = []
    for drop in get_ci(campaign, "timeBasedDrops") or []:
        for edge in get_ci(drop, "benefitEdges") or []:
            benefit = get_ci(edge, "benefit") or {}
            rows.append(
                {
                    "drop": get_ci(drop, "name"),
                    "benefit": get_ci(benefit, "name"),
                    "type": get_ci(benefit, "distributionType"),
                    "minutes": get_ci(drop, "requiredMinutesWatched"),
                }
            )
    return rows


def print_rows(rows, indent):
    if not rows:
        print(f"{indent}(none present in this response)")
        return
    for r in rows:
        print(
            f"{indent}{str(r['type']):<22} drop={r['drop']!r} "
            f"benefit={r['benefit']!r} req={r['minutes']}min"
        )


def main():
    parser = argparse.ArgumentParser(description="List active Twitch drop campaigns and their reward types.")
    parser.add_argument("--config", default="deploy/config/config.json")
    parser.add_argument("--user", default=None, help="Twitch login to query (default: all enabled)")
    parser.add_argument("--collection", default=None, help="Path to Twitch.postman_collection.json")
    parser.add_argument("--json", action="store_true", help="Dump raw dashboard JSON")
    args = parser.parse_args()

    root = pathlib.Path(__file__).resolve().parent.parent
    collection = args.collection or str(root / "TwitchDropsBot.Core" / "Postman" / "Twitch.postman_collection.json")
    hashes = find_hashes(collection)
    print(f"Using collection: {collection}")

    if not pathlib.Path(args.config).is_file():
        print(f"Config not found: {args.config}", file=sys.stderr)
        return 1

    users = load_users(args.config)
    if args.user:
        users = [u for u in users if (u.get("Login") or "").lower() == args.user.lower()]
    if not users:
        print("No matching enabled Twitch user(s) found.", file=sys.stderr)
        return 1

    for user in users:
        login = user.get("Login")
        user_id = user.get("Id")
        token = user.get("ClientSecret")
        print(f"\n===== {login} ({user_id}) =====")
        if not token:
            print("  No token in config.")
            continue

        try:
            dash = gql("ViewerDropsDashboard", {"fetchRewardCampaigns": True}, token, hashes)
        except urllib.error.HTTPError as exc:
            print(f"  HTTP {exc.code}: {exc.read().decode()[:300]}")
            continue

        if dash.get("errors"):
            print(f"  dashboard errors: {dash['errors']}")

        current_user = get_ci(dash.get("data") or {}, "currentUser") or {}
        campaigns = get_ci(current_user, "dropCampaigns") or []
        reward_campaigns = get_ci(dash.get("data") or {}, "rewardCampaignsAvailableToUser") or []
        print(f"  {len(campaigns)} active drop campaign(s), {len(reward_campaigns)} reward campaign(s)")

        for campaign in campaigns:
            name = get_ci(campaign, "name")
            game = get_ci(get_ci(campaign, "game") or {}, "displayName")
            status = get_ci(campaign, "status")
            end_at = get_ci(campaign, "endAt")
            dash_rows = reward_rows(campaign)
            print(f"\n  - {name} [{game}] status={status} endAt={end_at}")
            print("    ViewerDropsDashboard:")
            print_rows(dash_rows, "      ")

            details = gql(
                "DropCampaignDetails",
                {"dropID": get_ci(campaign, "id"), "channelLogin": user_id},
                token,
                hashes,
            )
            detail_campaign = get_ci(get_ci(details.get("data") or {}, "user") or {}, "dropCampaign") or {}
            det_rows = reward_rows(detail_campaign)
            print("    DropCampaignDetails:")
            print_rows(det_rows, "      ")

        if args.json:
            print("\n----- raw dashboard JSON -----")
            print(json.dumps(dash, indent=2)[:40000])

    return 0


if __name__ == "__main__":
    sys.exit(main())
