"""Pure Discogs matching rules: exact release match, cross-edition agreement, fill-blanks plan."""
import re
import unicodedata


def norm(value):
    return " ".join(unicodedata.normalize("NFC", str(value or "")).casefold().split())


def clean_name(name):
    """Drop Discogs disambiguation ("Polisen (2)") and name-variation star."""
    return re.sub(r"\s*\(\d+\)$", "", (name or "").strip()).rstrip("*")


def norm_artist(name):
    return norm(clean_name(name))


def release_artist(release):
    parts = []
    for a in release.get("artists", []):
        join = (a.get("join") or "").strip()
        parts.append(clean_name(a.get("name")) + ("," if join == "," else f" {join}" if join else ""))
    return " ".join(parts)


def tracks(release):
    for track in release.get("tracklist", []):
        if track.get("type_") == "track":
            yield track
        for sub in track.get("sub_tracks", []) or []:
            if sub.get("type_", "track") == "track":
                yield sub


INVISIBLE = dict.fromkeys(map(ord, "​‌‍⁠﻿"))
QUOTES = str.maketrans("‘’“”", "''\"\"")
ARTIST_SPLIT = re.compile(r"\s*(?:&|,|/|\+|\band\b|\bx\b|\bvs\.?|\bfeat\.?|\bft\.?|\bfeaturing\b)\s*")


def match_text(value):
    """Comparison form: invisible chars and curly quotes removed, '&' read as 'and', spacing around dots ignored."""
    text = norm(str(value or "").translate(INVISIBLE).translate(QUOTES)).replace("&", " and ")
    return " ".join(re.sub(r"\s*\.\s*", ".", text).split())


def match_album(value):
    return re.sub(r"\s+-\s+(single|ep)$", "", match_text(value))


def match_track(value):
    return re.sub(r"\s*(\((original|original mix)\)|-\s+original mix)$", "", match_text(value))


def match_artist(name):
    value = match_text(clean_name(name))
    return "various" if value in ("various", "various artists") else value


def artist_parts(name):
    return {p for p in (match_artist(x) for x in ARTIST_SPLIT.split(match_text(name))) if p}


def artists_match(release, artist):
    if match_artist(release_artist(release)) == match_artist(artist):
        return True
    ours = artist_parts(artist)
    theirs = {match_artist(a.get("name")) for a in release.get("artists", [])}
    return bool(ours and theirs) and (ours <= theirs or theirs <= ours)


def release_matches(release, artist, album, song_titles):
    if not artists_match(release, artist) or match_album(release.get("title")) != match_album(album):
        return False
    titles = {match_track(t.get("title")) for t in tracks(release)}
    return bool(song_titles) and all(match_track(t) in titles for t in song_titles)


def _fields(release):
    out = {}
    labels = [l for l in release.get("labels", []) if l.get("name") and l["name"] != "Not On Label"]
    if labels:
        out["label"] = "; ".join(dict.fromkeys(clean_name(l["name"]) for l in labels))
        numbers = {l.get("catno") for l in labels if l.get("catno") and norm(l["catno"]) not in ("none", "[none]")}
        if len(numbers) == 1:
            out["catno"] = numbers.pop()
    if isinstance(release.get("year"), int) and release["year"] > 0:
        out["year"] = release["year"]
    if release.get("country"):
        out["country"] = release["country"]
    return out


def agreed_fields(releases):
    """Fields that every matched edition states identically."""
    if not releases:
        return {}
    first, *rest = [_fields(r) for r in releases]
    return {k: v for k, v in first.items() if all(f.get(k) == v for f in rest)}


def master_fields(main_release, master, editions):
    """Many editions: label only if every edition lists it, year from the master (original release)."""
    out = {}
    labels = [clean_name(l["name"]) for l in main_release.get("labels", []) if l.get("name") and l["name"] != "Not On Label"]
    shared = [l for l in dict.fromkeys(labels)
              if all(norm(l) in {norm(clean_name(x)) for x in e.get("label", [])} for e in editions)]
    if shared:
        out["label"] = "; ".join(shared)
    if isinstance(master.get("year"), int) and master["year"] > 0:
        out["year"] = master["year"]
    return out


def _credit(entry):
    return {"name": clean_name(entry.get("name")),
            "role": entry.get("role", "").strip(), "discogsArtistId": entry.get("id")}


def _credits_one(release, title):
    match = [t for t in tracks(release) if norm(t.get("title")) == norm(title)]
    if len(match) != 1:
        return None
    track = match[0]
    found = [_credit(e) for e in track.get("extraartists", []) or []]
    for e in release.get("extraartists", []):
        spec = (e.get("tracks") or "").strip()
        if not spec or track.get("position") in [p.strip() for p in spec.split(",")]:
            found.append(_credit(e))
    return found


def track_credits(releases, title):
    """Credits for one track; with several editions, only if all editions agree."""
    if isinstance(releases, dict):
        return _credits_one(releases, title)
    results = [_credits_one(r, title) for r in releases]
    if not results or any(r is None for r in results):
        return None
    key = lambda c: sorted((x["name"], x["role"]) for x in c)
    return results[0] if all(key(r) == key(results[0]) for r in results[1:]) else None


def plan_song_update(song, fields):
    """Fill blank song fields; report non-blank values that differ."""
    sets, conflicts = {}, {}
    for key, value in fields.items():
        if key not in song:
            continue
        current = song[key]
        if current is None or norm(current) == "":
            sets[key] = value
        elif norm(current) != norm(value):
            conflicts[key] = {"current": current, "discogs": value}
    return sets, conflicts
