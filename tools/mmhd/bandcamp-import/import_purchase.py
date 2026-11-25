import argparse
from contextlib import contextmanager
import fcntl
import json
import os
from pathlib import Path
import shutil
import tempfile
import zipfile
import zlib

APP = Path(__file__).resolve().parents[3] / '.mmhd-local' / 'state' / 'bandcamp-import'
SOURCE = Path.home() / 'Music' / 'Dropbox-Musik' / 'Bandcamp'
DOWNLOADS = Path.home() / '.t3' / 'userdata' / 'server-browser' / 'downloads'
STATE = APP / 'state.json'


def state():
    return json.loads(STATE.read_text()) if STATE.exists() else {"imported": {}}


def save(value):
    with tempfile.NamedTemporaryFile(mode='w', dir=APP, delete=False) as handle:
        json.dump(value, handle, indent=2)
    os.replace(handle.name, STATE)


@contextmanager
def open_purchase(archive, filename):
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as purchase:
            yield purchase
        return
    if not filename or Path(filename).suffix.lower() != '.flac':
        raise ValueError('Expected a FLAC download or ZIP archive')
    with tempfile.TemporaryFile(dir=APP) as packaged:
        with zipfile.ZipFile(packaged, 'w') as purchase:
            purchase.write(archive, Path(filename).name)
        with zipfile.ZipFile(packaged) as purchase:
            yield purchase


def import_purchase(item, album, archive, filename=None):
    if not item.isdecimal() or not album or album in {'.', '..'} or album.startswith('.') or '/' in album or '\\' in album:
        raise ValueError('Invalid item ID or album folder')
    archive = archive.resolve(strict=True)
    if not archive.is_relative_to(DOWNLOADS.resolve()) or not archive.is_file():
        raise ValueError('Use a completed T3 browser download')
    with (APP / 'import.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        value = state()
        if item in value['imported']:
            print('Already imported')
            return
        destination = SOURCE / album
        with open_purchase(archive, filename) as z:
            tracks = [f for f in z.infolist() if not f.is_dir() and Path(f.filename).suffix.lower() == '.flac']
            names = [Path(f.filename).name for f in tracks]
            if not names or len({name.casefold() for name in names}) != len(names):
                raise ValueError('Expected a FLAC archive with unique track names')
            SOURCE.mkdir(parents=True, exist_ok=True)
            if destination.exists():
                for track, name in zip(tracks, names):
                    path = destination / name
                    if not path.is_file() or path.stat().st_size != track.file_size:
                        raise FileExistsError('Album folder already exists with different contents')
                    with path.open('rb') as file:
                        crc = 0
                        for block in iter(lambda: file.read(1024 * 1024), b''):
                            crc = zlib.crc32(block, crc)
                    if crc != track.CRC:
                        raise FileExistsError('Album folder already exists with different contents')
            else:
                with tempfile.TemporaryDirectory(prefix='.incoming-', dir=SOURCE) as temporary:
                    staged = Path(temporary) / 'album'
                    staged.mkdir()
                    for track, name in zip(tracks, names):
                        with z.open(track) as source, (staged / name).open('xb') as target:
                            header = source.read(4)
                            if header != b'fLaC':
                                raise ValueError('Archive contains a non-FLAC track')
                            target.write(header)
                            shutil.copyfileobj(source, target)
                    staged.rename(destination)
        value['imported'][item] = {'album': album, 'tracks': len(tracks)}
        save(value)
        print(f'Imported {len(tracks)} tracks from {album}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('item', nargs='?')
    parser.add_argument('album', nargs='?')
    parser.add_argument('archive', nargs='?', type=Path)
    parser.add_argument('--filename')
    args = parser.parse_args()
    if args.item is None:
        print(json.dumps(state()))
    elif args.album and args.archive:
        import_purchase(args.item, args.album, args.archive, args.filename)
    else:
        parser.error('Supply item ID, album folder, and archive path together')
