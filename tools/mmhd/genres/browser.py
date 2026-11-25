from html import escape
from urllib.parse import urlencode, parse_qs
from pathlib import Path
import json,sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'labels'))
from backfill import sql,literal

def render(query):
    args=parse_qs(query)
    style=args.get('style',[''])[0]
    search=args.get('q',[''])[0].strip()
    scope='''from "Songs" s cross join lateral regexp_split_to_table(s."Genre",';') g where s."OwnerUserId"='9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f' and s."DeletedAtUtc" is null and trim(g)<>'' '''
    if style:
        query='''select distinct s."Id",s."Album" album,coalesce(nullif(s."AlbumArtist",''),s."Artist") aa,s."Artist" artist,s."Title" title,s."TrackNumber" '''+scope+' and lower(trim(g))=lower('+literal(style)+') order by album,aa,s."TrackNumber",title'
    else:
        query='select min(trim(g)) name,count(distinct s."Id") tracks '+scope+' and strpos(lower(trim(g)),lower('+literal(search)+'))>0 group by lower(trim(g)) order by lower(trim(g))'
    rows=json.loads(sql('select coalesce(json_agg(r),\'[]\') from ('+query+') r'))
    def e(v):return escape(str(v or ''))
    body=['<nav><a href="/">Dashboard</a> / <a href="/genres">Genres &amp; styles</a></nav>']
    if style:
        body.append(f'<h1>{e(style)}</h1><p>{len(rows)} matching tracks</p>')
        album=None
        for r in rows:
            key=(r['album'],r['aa'])
            if key!=album:
                body.append(f'<h2>{e(r["album"] or "Unknown album")}</h2><small>{e(r["aa"])}</small>')
                album=key
            body.append(f'<p>{e(r["title"])} · {e(r["artist"])}</p>')
    else:
        body.extend(['<h1>Genres &amp; styles</h1>',f'<form><label>Find a style <input type="search" name="q" value="{e(search)}" placeholder="Acid House"></label> <button>Search</button></form>'])
        for r in rows:
            href='/genres?'+urlencode({'style':r['name']})
            body.append(f'<a class="row" href="{e(href)}">{e(r["name"])} <small>{r["tracks"]} tracks</small></a>')
        if not rows:body.append('<p>No matching styles.</p>')
    return ('<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Genres &amp; styles · MusicHoarder</title><style>html{color-scheme:light dark}body{font:1rem/1.5 system-ui;max-width:48rem;margin:2rem auto;padding:0 1rem}h2{margin-bottom:0}a{color:LinkText}.row{display:flex;justify-content:space-between;padding:.65rem 0;border-bottom:1px solid GrayText}input,button{font:inherit}small{opacity:.8}</style><main>'+''.join(body)+'</main></html>').encode()
