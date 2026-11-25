"""Merge explicit style credits from source files, matched results and verified editions."""
import datetime
import email.utils
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections import defaultdict

from mutagen import File, MutagenError

ROOT = Path(__file__).resolve().parents[3] / '.mmhd-local' / 'state' / 'genres'
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'labels'))
from backfill import sql, literal, norm

os.umask(0o077)
db = sqlite3.connect(ROOT / 'genres.sqlite')
db.executescript("""
create table if not exists editions(provider integer,id text,checked real,data text,primary key(provider,id));
create table if not exists pauses(provider integer primary key,until real);
create table if not exists changes(at real,song integer,previous text,current text,evidence text);
""")
owner = '9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f'
def rows(query):
    return json.loads(sql(f"select coalesce(json_agg(r),'[]') from ({query}) r"))

def file_genres(path):
    try:
        audio = File(path)
        if audio is None or not audio.tags:
            return []
        result = []
        for key, value in audio.tags.items():
            if str(key).casefold().split(':')[-1] not in ('tcon','©gen','genre','style','discogs_album_genre','discogs_album_style'):
                continue
            values = getattr(value, 'genres', getattr(value, 'text', value))
            for raw in values if isinstance(values, (tuple,list)) else [values]:
                if isinstance(raw, bytes):
                    raw = raw.decode('utf-8', errors='replace')
                result.extend(str(raw).split(';'))
        return result
    except (OSError,MutagenError,ValueError):
        return []

def fetch(provider, ident):
    if not ident.isdigit():
        return []
    cached = db.execute('select data from editions where provider=? and id=? and checked>?',(provider,ident,time.time()-2592000)).fetchone()
    if cached: return json.loads(cached[0])
    if budget[provider] <= 0 or db.execute('select 1 from pauses where provider=? and until>?',(provider,time.time())).fetchone():
        return []
    budget[provider] -= 1
    time.sleep(3)
    url = ('https://itunes.apple.com/lookup?entity=song&id=' if provider==5 else 'https://api.deezer.com/album/') + ident
    try:
        with urllib.request.urlopen(url,timeout=8) as response:
            j=json.load(response)
        if provider==5:
            records=[(t.get('collectionName'),t.get('trackName'),t.get('artistName'),[t.get('primaryGenreName')]) for t in j.get('results',[]) if str(t.get('collectionId'))==ident and t.get('kind')=='song']
        else:
            if j.get('error'):
                raise ValueError('provider error')
            styles=[g.get('name') for g in (j.get('genres') or {}).get('data',[])]
            records=[(j.get('title'),t.get('title'),(t.get('artist') or {}).get('name'),styles) for t in (j.get('tracks') or {}).get('data',[]) if str(j.get('id'))==ident]
        db.execute('insert or replace into editions values (?,?,?,?)',(provider,ident,time.time(),json.dumps(records)))
    except (OSError,ValueError) as e:
        delay=1800
        if isinstance(e,urllib.error.HTTPError):
            retry=e.headers.get('Retry-After','')
            if retry:
                try: delay=max(delay,float(retry))
                except ValueError: delay=max(delay,email.utils.parsedate_to_datetime(retry).timestamp()-time.time())
        db.execute('insert or replace into pauses values (?,?)',(provider,time.time()+delay))
        print('Paused provider',provider,getattr(e,'code',type(e).__name__),flush=True)
        records=[]
    db.commit()
    return records

songs=rows(f'''select "Id" id,"Genre" genre,"Tags" tags,"Title" title,"Artist" artist,"Album" album,"AlbumArtist" aa,"SourcePath" src,"DestinationPath" dest from "Songs" where "DeletedAtUtc" is null and "OwnerUserId"={literal(owner)} order by "Id"''')
evidence=defaultdict(list)
for r in rows(f'''select s."Id" id,a."Provider" provider,a."MatchedDataJson"::jsonb->>'Genre' genre from "SongProviderAttempts" a join "Songs" s on s."Id"=a."SongId" where a."Status"=1 and s."OwnerUserId"={literal(owner)} and lower(trim(a."MatchedDataJson"::jsonb->>'Artist'))=lower(trim(s."Artist")) and lower(trim(a."MatchedDataJson"::jsonb->>'Title'))=lower(trim(s."Title")) and nullif(a."MatchedDataJson"::jsonb->>'Genre','') is not null'''):
    evidence[r['id']].append((str(r['provider']),r['genre'].split(';')))
editions=defaultdict(list)
for r in rows('''select "DisplayTitle" title,"DisplayArtist" artist,"SourcesJson"::jsonb sources from "CanonicalAlbums" where "SourcesJson" is not null'''):
    editions[norm(r['title']),norm(r['artist'])].extend((s['Provider'],str(s['AlbumId'])) for s in r['sources'] or [] if s.get('InWinningCluster') and s.get('Provider') in (4,5))
budget={4:10,5:10}
changes=[]
for s in songs:
    credits=evidence[s['id']]
    for field,prefix,directory in [('src','/music/source/','Dropbox-Musik'),('dest','/music/destination/','MusicHoarder')]:
        path=s[field] or ''
        if path.startswith(prefix):
            base=Path.home()/'Music'/directory
            local=(base/path[len(prefix):]).resolve()
            if base in local.parents:
                credits.append((field,file_genres(local)))
    for provider,ident in editions.get((norm(s['album']),norm(s['aa'] or s['artist'])),[]):
        for album,title,artist,genres in fetch(provider,ident):
            if (norm(album),norm(title),norm(artist))==(norm(s['album']),norm(s['title']),norm(s['artist'])):
                credits.append((f'{provider}/{ident}',genres))
    tags = list(s['tags'] or [])
    tag_keys = {norm(tag) for tag in tags}
    for source, values in credits:
        # Destination tags are generated, and Discogs (provider 7) belongs to the taxonomy.
        if source in ('dest', '7'):
            continue
        for value in values:
            for part in re.split(r'[;|\r\n]+', value or ''):
                tag = part.strip()
                if tag and len(tag) <= 100 and norm(tag) not in tag_keys and not re.search(r'[\x00-\x1f]|https?://', tag):
                    tags.append(tag)
                    tag_keys.add(norm(tag))
    current=[x.strip() for x in (s['genre'] or '').split(';') if x.strip()]
    seen={norm(x) for x in current}
    additions={}
    for source,values in credits:
        for v in values:
            if not v or not v.strip() or norm(v) in seen or len(v)>100 or re.search(r'[\x00-\x1f]|https?://',v): continue
            additions[v.strip()]=source
            seen.add(norm(v))
    if additions or current or tags != s['tags']:
        updated='; '.join(current+list(additions))
        changes.append((s,updated,additions,tags))
# Normalize once per distinct value, including legacy compound fields already in the library.
mapper = Path(__file__).resolve().parents[3] / 'scripts' / 'discogs-genre-map.ts'
raw_values = sorted({value for _, value, _, _ in changes})
canonical = json.loads(subprocess.check_output(
    ['bun', str(mapper)], input=json.dumps(raw_values), text=True
))
# One conditional batch also protects additions made by the API while files were being read.
pending = []
for song, raw, added, tags in changes:
    genre = canonical[raw]
    if genre != song['genre'] or tags != song['tags']:
        pending.append({**song, 'updated': genre, 'added': added, 'new_tags': tags})
if pending:
    payload = literal(json.dumps(pending, ensure_ascii=False))
    applied = sql(f'''with incoming as (
        select * from jsonb_to_recordset({payload}::jsonb) as x(
            id int, genre text, updated text, tags text[], new_tags text[], artist text, title text, album text)
    ) update "Songs" s set "Genre"=i.updated, "Tags"=i.new_tags from incoming i
    where s."Id"=i.id and s."OwnerUserId"={literal(owner)} and s."DeletedAtUtc" is null
        and s."Genre" is not distinct from i.genre and s."Tags"=i.tags
        and s."Artist" is not distinct from i.artist and s."Title" is not distinct from i.title
        and s."Album" is not distinct from i.album returning s."Id"''')
    ids = {int(row) for row in applied.splitlines() if row.strip()}
    db.executemany('insert into changes values (?,?,?,?,?)', [
        (time.time(), s['id'], s['genre'], s['updated'], json.dumps(s['added']))
        for s in pending if s['id'] in ids
    ])
    db.commit()
    print('Genre and tag merge finished', len(ids), flush=True)
else:
    print('Genre and tag merge finished', 0, flush=True)
