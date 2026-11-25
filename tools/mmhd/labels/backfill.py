"""Fill missing label credits from exact MusicBrainz release editions on this installation."""
import datetime
import email.utils
import fcntl
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import time
import unicodedata
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[3] / '.mmhd-local' / 'state' / 'labels'
USER_AGENT = "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)"
def sql(command):
    args = ["/opt/homebrew/bin/docker","exec","-i","musichoarder-postgres-1","sh","-c",
            'PGPASSWORD="$POSTGRES_PASSWORD" psql -XqAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d musichoarderdb']
    return subprocess.check_output(args, input=command, text=True).strip()

def literal(value):
    return "NULL" if value is None else "'" + str(value).replace("'","''") + "'"

def norm(value):
    return " ".join(unicodedata.normalize("NFC", value or "").casefold().split())

def get_json(url, state):
    headers = {"User-Agent": USER_AGENT}
    token = ROOT / ".discogs-token"
    if url.startswith("https://api.discogs.com/") and token.exists():
        headers["Authorization"] = "Discogs token=" + token.read_text().strip()
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=12) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return {}
        if error.code in (429,503):
            retry = error.headers.get("Retry-After","60")
            until = time.time()+int(retry) if retry.isdigit() else email.utils.parsedate_to_datetime(retry).timestamp()
            state.execute("insert or replace into pause values (1,?)",(max(time.time()+1,until),))
            state.commit()
        raise

def run():
    state = sqlite3.connect(ROOT / "credits.sqlite")
    state.executescript("""create table if not exists releases(id text primary key, checked real, data text);
        create table if not exists attempts(id text primary key, at real);
        create table if not exists pause(id integer primary key, until real);""")
    if state.execute("select 1 from pause where until>?",(time.time(),)).fetchone():
        return
    rows = json.loads(sql("""select coalesce(json_agg(json_build_object('id',"Id",'album',"Album",'artist',"Artist",
      'title',"Title",'rec',"MusicBrainzId",'mbid',"MusicBrainzReleaseId")), '[]') from "Songs"
      where "DeletedAtUtc" is null and "OwnerUserId"='9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f'
      and nullif(trim("Label"),'') is null and "MusicBrainzReleaseId" is not null and "LibraryBuildStatus" in(0,3);"""))
    groups = {}
    for row in rows:
        groups.setdefault(row["mbid"],[]).append(row)
    total = 0
    attempted=dict(state.execute("select id,at from attempts"))
    for mbid in sorted(groups,key=lambda k:attempted.get(k,0)):
        songs=groups[mbid]
        try:
            uuid.UUID(mbid)
        except ValueError:
            continue
        cached = state.execute("select data from releases where id=? and checked>?",(mbid,time.time()-2592000)).fetchone()
        if cached:
            j=json.loads(cached[0])
        else:
            state.execute("insert or replace into attempts values (?,?)",(mbid,time.time()))
            state.commit()
            time.sleep(1.5)
            j=get_json(f"https://musicbrainz.org/ws/2/release/{mbid}?inc=labels+artist-credits+recordings+url-rels&fmt=json",state)
            credits=[(v['label']['name'],v.get('catalog-number')) for v in j.get('label-info',[]) if v.get('label') and v['label']['name'] not in ('[no label]','[unknown]')]
            if not credits and j.get('id')==mbid:
                urls={m[1] for r in j.get('relations',[]) if r.get('type')=='discogs' and (m:=re.fullmatch(r'https://(?:www\.)?discogs\.com/release/(\d+)(?:-[^/]*)?/?',r.get('url',{}).get('resource','')))}
                if len(urls)==1:
                    did=urls.pop()
                    time.sleep(3)
                    d=get_json('https://api.discogs.com/releases/'+did,state)
                    if str(d.get('id'))==did and norm(d.get('title'))==norm(j.get('title')):
                        credits=[(v['name'],v.get('catno')) for v in d.get('labels',[]) if v.get('name') and v['name']!='Not On Label']
                        j['discogsReleaseId']=did
            j['verifiedCredits']=credits
            state.execute("insert or replace into releases values (?,?,?)",(mbid,time.time(),json.dumps(j)))
            state.commit()
        credits=j.get('verifiedCredits',[])
        if not credits or j.get('id')!=mbid:continue
        label='; '.join(dict.fromkeys(v[0] for v in credits))
        numbers={v[1] for v in credits if v[1] and v[1].casefold() not in ('none','[none]')}
        catalog=numbers.pop() if len(numbers)==1 else None
        tracks=[t for m in j.get('media',[]) for t in m.get('tracks',[])]
        for s in songs:
            if norm(s['album'])!=norm(j.get('title')):continue
            if not any(norm(t.get('title'))==norm(s['title']) and ((s['rec'] and s['rec']==t.get('recording',{}).get('id')) or norm(s['artist'])==norm(''.join(a['name']+a.get('joinphrase','') for a in t.get('artist-credit',[])))) for t in tracks):continue
            result=sql(f"""update "Songs" set "Label"={literal(label)},"CatalogNumber"=coalesce(nullif(trim("CatalogNumber"),''),{literal(catalog)}),
              "PreviousDestinationPath"=case when "LibraryBuildStatus"=3 then "DestinationPath" else "PreviousDestinationPath" end,
              "LibraryBuildStatus"=0,"LibraryBuildAttempts"=0,"LibraryBuildLastAttemptedAtUtc"=null,"LibraryBuildError"=null
              where "Id"={int(s['id'])} and "MusicBrainzReleaseId"={literal(mbid)} and "Album"={literal(s['album'])}
              and "Title"={literal(s['title'])} and "Artist"={literal(s['artist'])} and "DeletedAtUtc" is null
              and nullif(trim("Label"),'') is null and "LibraryBuildStatus" in(0,3) returning "Id";""")
            total+=bool(result)
    print(f"Filled {total} tracks",flush=True)

if __name__=='__main__':
    os.umask(0o077)
    with (ROOT / "backfill.lock").open("a") as lock:
        try:fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:raise SystemExit(0)
        try:
            from catalog_profiles import run_catalog
            run_catalog()
            run()
        except urllib.error.HTTPError as error:
            print(f"Lookup paused after HTTP {error.code}",flush=True)
            if error.code not in (429,503):raise
