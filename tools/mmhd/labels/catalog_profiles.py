"""Link exact release credits to library tracks and maintain reusable label profiles."""
import json
import re
import sqlite3
import subprocess
from pathlib import Path
import time
import urllib.error
from urllib.parse import quote
from backfill import ROOT, get_json, literal, norm, sql

OWNER = "9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f"


def enrich_upcs(state):
    rows = json.loads(sql(f"""select coalesce(json_agg(json_build_object('id',"Id",'upc',"Upc",
        'artist',"Artist",'album',"Album",'title',"Title",'isrc',"Isrc")), '[]') from "Songs"
        where "OwnerUserId"={literal(OWNER)} and "DeletedAtUtc" is null
        and nullif(trim("Label"),'') is null and "LibraryBuildStatus" in (0,3)
        and "Upc" ~ '^[0-9]{{8,14}}$' """))
    groups = {}
    for row in rows:
        groups.setdefault(row['upc'], []).append(row)
    checked = dict(state.execute('select id,at from attempts'))
    pending = sorted(groups, key=lambda u: checked.get('upc:'+u, 0))
    for upc in pending[:30]:
        if checked.get('upc:'+upc, 0) > time.time()-86400:
            continue
        state.execute('insert or replace into attempts values (?,?)', ('upc:'+upc,time.time()))
        state.commit()
        time.sleep(1.5)
        album = get_json('https://api.deezer.com/album/upc:'+upc, state)
        if album.get('error'):
            if album['error'].get('code') != 800:
                break
        label = (album.get('label') or '').strip()
        if not label or str(album.get('upc','')).lstrip('0') != upc.lstrip('0'):
            continue
        for song in groups[upc]:
            matches = [t for t in album.get('tracks',{}).get('data',[]) if
                       norm(t.get('title')) == norm(song['title']) and
                       norm(t.get('artist',{}).get('name')) == norm(song['artist'])]
            if len(matches)!=1 or norm(album.get('title')) != norm(song['album']):
                continue
            sql(f"""update "Songs" set "Label"={literal(label)},
                "PreviousDestinationPath"=case when "LibraryBuildStatus"=3 then "DestinationPath" else "PreviousDestinationPath" end,
                "LibraryBuildStatus"=0,"LibraryBuildAttempts"=0,"LibraryBuildLastAttemptedAtUtc"=null,"LibraryBuildError"=null
                where "OwnerUserId"={literal(OWNER)} and "Id"={int(song['id'])} and "DeletedAtUtc" is null
                and nullif(trim("Label"),'') is null and "LibraryBuildStatus" in(0,3)
                and "Upc"={literal(upc)} and "Artist"={literal(song['artist'])}
                and "Title"={literal(song['title'])} and "Album"={literal(song['album'])}""")


def profiles(state):
    sql("""create table if not exists mh_label_profiles (
        name_key text primary key, name text not null, metadata jsonb not null);
        create or replace view mh_track_labels as
        select s."Id" as track_id,s."OwnerUserId" as owner_id,trim(credit) as label,p.metadata
        from "Songs" s cross join lateral unnest(string_to_array(s."Label",';')) credit
        left join mh_label_profiles p on p.name_key=lower(trim(credit))
        where s."DeletedAtUtc" is null and trim(credit)<>'';""")
    names=json.loads(sql(f"""select coalesce(json_agg(distinct label),'[]') from mh_track_labels
                             where owner_id={literal(OWNER)}"""))
    # A matching label ID from a verified release establishes the label identity.
    identities={}
    for (blob,) in state.execute('select data from releases'):
        release=json.loads(blob)
        for entry in release.get('label-info', []):
            label=entry.get('label') or {}
            if label.get('id') and label.get('name'):
                identities.setdefault(norm(label['name']),set()).add(label['id'])
    for name in names:
        ids=identities.get(norm(name),set())
        if len(ids)!=1:
            continue
        mid=next(iter(ids))
        key='profile:'+mid
        if state.execute('select 1 from attempts where id=? and at>?',(key,time.time()-604800)).fetchone():
            continue
        time.sleep(1.5)
        profile=get_json('https://musicbrainz.org/ws/2/label/'+quote(mid,safe='')+'?inc=url-rels&fmt=json',state)
        if profile.get('id')!=mid or norm(profile.get('name'))!=norm(name):
            continue
        data={'musicbrainzId':mid,'type':profile.get('type'),'country':profile.get('country'),'area':(profile.get('area') or {}).get('name'),
              'founded':(profile.get('life-span') or {}).get('begin')}
        for rel in profile.get('relations',[]):
            url=rel.get('url',{}).get('resource','')
            match=re.fullmatch(r'https://(?:www\.)?discogs\.com/label/(\d+)(?:-[^/]*)?/?',url)
            if rel.get('type')=='discogs' and match:
                discogs=get_json('https://api.discogs.com/labels/'+match[1],state)
                if str(discogs.get('id'))==match[1]:
                    images=discogs.get('images') or []
                    data.update(discogsId=match[1],discogsUrl=url,description=discogs.get('profile'),
                                image=(next((i for i in images if i.get('type')=='primary'),{}) or {}).get('uri'))
                break
        sql(f"""insert into mh_label_profiles values(lower({literal(name)}),{literal(name)},{literal(json.dumps(data))}::jsonb)
                on conflict(name_key) do update set metadata=excluded.metadata""")
        state.execute('insert or replace into attempts values (?,?)',(key,time.time()))
        state.commit()


def embedded_credits(state):
    """Read explicit credits from the original file when the library has no label."""
    rows=json.loads(sql(f"""select coalesce(json_agg(json_build_object('id',"Id",'path',"SourcePath")),'[]')
        from "Songs" where "OwnerUserId"={literal(OWNER)} and "DeletedAtUtc" is null
        and "LibraryBuildStatus" in (0,3) and nullif(trim("Label"),'') is null"""))
    roots={'/music/source/':Path.home()/'Music/Dropbox-Musik',
           '/data/slskd-downloads/':ROOT.parent/'slskd-downloads'}
    state.execute('create table if not exists file_checks(path text primary key,mtime integer)')
    checked=dict(state.execute('select path,mtime from file_checks'))
    for row in rows:
        source=row['path'] or ''
        for prefix,root in roots.items():
            if source.startswith(prefix):
                path=(root/source[len(prefix):]).resolve()
                break
        else:
            continue
        if not path.is_relative_to(root.resolve()) or not path.is_file():
            continue
        mtime=path.stat().st_mtime_ns
        if checked.get(source)==mtime:
            continue
        try:
            probe=subprocess.run(['/opt/homebrew/bin/ffprobe','-v','error','-show_entries',
                'format_tags','-of','json',str(path)],capture_output=True,timeout=15)
        except (subprocess.TimeoutExpired,OSError):
            continue
        if probe.returncode:
            continue
        tags={k.casefold():v.strip() for k,v in json.loads(probe.stdout).get('format',{}).get('tags',{}).items()}
        label=tags.get('label') or tags.get('recordlabel') or tags.get('organization')
        if label and norm(label) not in ('unknown','[unknown]','[no label]','none'):
            sql(f"""update "Songs" set "Label"={literal(label)},
                "PreviousDestinationPath"=case when "LibraryBuildStatus"=3 then "DestinationPath" else "PreviousDestinationPath" end,
                "LibraryBuildStatus"=0,"LibraryBuildAttempts"=0,"LibraryBuildLastAttemptedAtUtc"=null,"LibraryBuildError"=null
                where "OwnerUserId"={literal(OWNER)} and "Id"={int(row['id'])} and "SourcePath"={literal(source)}
                and "DeletedAtUtc" is null and nullif(trim("Label"),'') is null and "LibraryBuildStatus" in(0,3)""")
        state.execute('insert or replace into file_checks values (?,?)',(source,mtime))
        state.commit()


def run_catalog():
    with sqlite3.connect(ROOT/'credits.sqlite') as state:
        embedded_credits(state)
        if state.execute('select 1 from pause where until>?',(time.time(),)).fetchone():
            return
        profiles(state)
        enrich_upcs(state)
