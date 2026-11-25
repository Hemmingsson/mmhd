"""Fill blank label, catalog number and year from exact Discogs releases; store release country and credits."""
import email.utils
import fcntl
import json
import os
import re
from pathlib import Path
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from backfill import literal, sql
from discogs_match import ARTIST_SPLIT, agreed_fields, master_fields, match_album, norm, plan_song_update, release_matches, track_credits

ROOT = Path(__file__).resolve().parents[3] / '.mmhd-local' / 'state' / 'labels'
OWNER = "9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f"
USER_AGENT = "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)"
MAX_REQUESTS = int(os.environ.get("DISCOGS_MAX_REQUESTS", "150"))
MAX_EDITIONS = 5
RECHECK = 30 * 86400
COLUMNS = {"label": "Label", "catno": "CatalogNumber", "year": "Year"}


class Paused(Exception):
    pass


class Client:
    def __init__(self, state):
        self.state = state
        self.token = (ROOT / ".discogs-token").read_text().strip()
        self.requests = 0
        self.last = {}

    def paused(self, provider):
        row = self.state.execute("select until from pause where provider=?", (provider,)).fetchone()
        return bool(row and row[0] > time.time())

    def pause(self, provider, retry):
        if retry and retry.strip().isdigit():
            until = time.time() + int(retry)
        else:
            try:
                until = email.utils.parsedate_to_datetime(retry).timestamp()
            except (TypeError, ValueError):
                until = time.time() + 60
        self.state.execute("insert or replace into pause values (?,?)", (provider, max(time.time() + 1, until)))
        self.state.commit()

    def get(self, provider, url, spacing):
        if self.paused(provider):
            raise Paused(provider)
        if provider == "discogs" and self.requests >= MAX_REQUESTS:
            raise Paused("budget")
        time.sleep(max(0, self.last.get(provider, 0) + spacing - time.time()))
        headers = {"User-Agent": USER_AGENT}
        if provider == "discogs":
            headers["Authorization"] = "Discogs token=" + self.token
            self.requests += 1
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=15) as response:
                self.last[provider] = time.time()
                if provider == "discogs" and int(response.headers.get("X-Discogs-Ratelimit-Remaining", "60")) < 5:
                    self.last[provider] = time.time() + 60
                return json.load(response)
        except urllib.error.HTTPError as error:
            self.last[provider] = time.time()
            if error.code == 404:
                return {}
            if error.code in (429, 503):
                self.pause(provider, error.headers.get("Retry-After"))
                raise Paused(provider) from None
            raise


def discogs_id_from_musicbrainz(client, mbid):
    try:
        uuid.UUID(mbid)
        j = client.get("musicbrainz", f"https://musicbrainz.org/ws/2/release/{mbid}?inc=url-rels&fmt=json", 1.5)
    except (ValueError, Paused):
        return None
    ids = {r["url"]["resource"].rstrip("/").split("/release/")[1].split("-")[0]
           for r in j.get("relations", []) if r.get("type") == "discogs" and "/release/" in r.get("url", {}).get("resource", "")}
    return ids.pop() if len(ids) == 1 else None


def find_releases(client, group):
    artist, album, titles, mbid = group["artist"], group["album"], group["titles"], group.get("mbid")
    if mbid:
        did = discogs_id_from_musicbrainz(client, mbid)
        if did:
            release = client.get("discogs", f"https://api.discogs.com/releases/{did}", 1.1)
            if release_matches(release, artist, album, titles):
                return [release], "musicbrainz-link", agreed_fields([release])
    title = re.sub(r"\s+-\s+(single|ep)$", "", album, flags=re.I)
    primary = ARTIST_SPLIT.split(artist)[0].strip()
    for query_artist in dict.fromkeys([artist, primary]):
        query = urllib.parse.urlencode({"type": "release", "artist": query_artist, "release_title": title, "per_page": 25})
        results = client.get("discogs", "https://api.discogs.com/database/search?" + query, 1.1).get("results", [])
        candidates = [r for r in results if match_album(r.get("title", "")).endswith(" - " + match_album(album))]
        if candidates:
            break
    if len(candidates) > MAX_EDITIONS:
        masters = {c.get("master_id") for c in candidates if c.get("master_id")}
        editions = [c for c in candidates if c.get("master_id") in masters]
        if len(masters) != 1:
            return [], f"review: {len(candidates)} editions, {len(masters)} masters", {}
        master = client.get("discogs", f"https://api.discogs.com/masters/{masters.pop()}", 1.1)
        main = client.get("discogs", f"https://api.discogs.com/releases/{master.get('main_release')}", 1.1)
        if not release_matches(main, artist, album, titles):
            return [], "review: master main release does not match", {}
        return [main], "master", master_fields(main, master, editions)
    matched = []
    for c in candidates:
        release = client.get("discogs", f"https://api.discogs.com/releases/{c['id']}", 1.1)
        if release_matches(release, artist, album, titles):
            matched.append(release)
    return matched, "search" if matched else "no exact match", agreed_fields(matched)


def candidate_groups():
    rows = json.loads(sql(f"""select coalesce(json_agg(json_build_object('id',s."Id",'artist',coalesce(nullif(trim(s."AlbumArtist"),''),s."Artist"),
      'album',s."Album",'title',s."Title",'mbid',s."MusicBrainzReleaseId",'label',s."Label",'catno',s."CatalogNumber",'year',s."Year")), '[]')
      from "Songs" s left join mh_song_discogs d on d.song_id=s."Id"
      where s."DeletedAtUtc" is null and s."OwnerUserId"='{OWNER}' and nullif(trim(s."Album"),'') is not null
      and nullif(trim(s."Artist"),'') is not null and s."LibraryBuildStatus" in (0,3)
      and (d.song_id is null or nullif(trim(s."Label"),'') is null or nullif(trim(s."CatalogNumber"),'') is null or s."Year" is null);"""))
    groups = {}
    for row in rows:
        key = norm(row["artist"]) + "\x1f" + norm(row["album"])
        g = groups.setdefault(key, {"key": key, "artist": row["artist"], "album": row["album"], "songs": [], "mbids": set()})
        g["songs"].append(row)
        if row["mbid"]:
            g["mbids"].add(row["mbid"])
    for g in groups.values():
        g["titles"] = [s["title"] for s in g["songs"]]
        g["mbid"] = g["mbids"].pop() if len(g["mbids"]) == 1 else None
    return list(groups.values())


def apply(group, releases, matched_by, fields, backup):
    fields = dict(fields)
    country = fields.pop("country", None)
    ids = [r["id"] for r in releases]
    filled = 0
    for song in group["songs"]:
        sets, conflicts = plan_song_update({k: song[k] for k in COLUMNS}, fields)
        credits = track_credits(releases, song["title"])
        if sets:
            backup.write(json.dumps({"at": time.time(), "songId": song["id"], "releaseIds": ids,
                                     "before": {COLUMNS[k]: song[k] for k in sets}, "after": {COLUMNS[k]: v for k, v in sets.items()}}) + "\n")
            backup.flush()
            assign = ",".join(f'"{COLUMNS[k]}"={int(v) if k == "year" else literal(v)}' for k, v in sets.items())
            guard = " and ".join(f'"{COLUMNS[k]}" is null' if k == "year" else f'nullif(trim("{COLUMNS[k]}"),\'\') is null' for k in sets)
            done = sql(f"""update "Songs" set {assign},
              "PreviousDestinationPath"=case when "LibraryBuildStatus"=3 then "DestinationPath" else "PreviousDestinationPath" end,
              "LibraryBuildStatus"=0,"LibraryBuildAttempts"=0,"LibraryBuildLastAttemptedAtUtc"=null,"LibraryBuildError"=null
              where "Id"={int(song['id'])} and "OwnerUserId"='{OWNER}' and "DeletedAtUtc" is null and "LibraryBuildStatus" in (0,3)
              and "Album"={literal(song['album'])} and "Title"={literal(song['title'])} and {guard} returning "Id";""")
            filled += bool(done)
        sql(f"""insert into mh_song_discogs values ({int(song['id'])},'{OWNER}',{literal(json.dumps(ids))}::jsonb,{literal(country)},
          {literal(None if credits is None else json.dumps(credits))}::jsonb,{literal(json.dumps(conflicts))}::jsonb,{literal(matched_by)},now())
          on conflict (song_id) do update set release_ids=excluded.release_ids, release_country=excluded.release_country,
          credits=excluded.credits, conflicts=excluded.conflicts, matched_by=excluded.matched_by, fetched_at=excluded.fetched_at;""")
    return filled


def run():
    state = sqlite3.connect(ROOT / "discogs.sqlite")
    state.executescript("""create table if not exists groups(key text primary key, checked real, outcome text);
        create table if not exists pause(provider text primary key, until real);""")
    sql("""set client_min_messages=warning; create table if not exists mh_song_discogs(song_id integer primary key, owner_id uuid not null, release_ids jsonb not null,
        release_country text, credits jsonb, conflicts jsonb not null, matched_by text not null, fetched_at timestamptz not null);""")
    client = Client(state)
    if client.paused("discogs"):
        print("Discogs paused", flush=True)
        return
    checked = dict(state.execute("select key, checked from groups"))
    groups = [g for g in candidate_groups() if checked.get(g["key"], 0) < time.time() - RECHECK]
    groups.sort(key=lambda g: checked.get(g["key"], 0))
    stats = {"groups": 0, "matched": 0, "filled": 0, "review": 0}
    with (ROOT / "discogs-backup.jsonl").open("a") as backup:
        try:
            for group in groups:
                releases, outcome, fields = find_releases(client, group)
                stats["groups"] += 1
                if releases:
                    stats["matched"] += 1
                    stats["filled"] += apply(group, releases, outcome, fields, backup)
                elif outcome.startswith("review"):
                    stats["review"] += 1
                state.execute("insert or replace into groups values (?,?,?)", (group["key"], time.time(), outcome))
                state.commit()
        except Paused as stop:
            print(f"Stopped: {stop} ({client.requests} Discogs requests)", flush=True)
    print(f"{time.strftime('%F %T')} Discogs: {stats} of {len(groups)} due", flush=True)


if __name__ == "__main__":
    os.umask(0o077)
    with (ROOT / "discogs.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            sys.exit(0)
        run()
