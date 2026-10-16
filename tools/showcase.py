"""Collects what was built into one folder with an index page: reload GIFs, pose sheet, before/after pictures.

  python tools/showcase.py OUT_DIR [--film DIR ...] [--before DIR --after DIR] [--hands PNG]

Each --film DIR adds its reload-*.gif and film-*-slow.gif files. --before/--after pair up the same picture names from two
weapon runs (magazine in the hand, rack grip close-ups) side by side. Nothing is downloaded; it only copies local files.
"""
import argparse
import html
import os
import re
import shutil
import sys

PAIRS = [
    ('mag-4-in-hand-', 'Fresh magazine in the hand'),
    ('mag-6-half-in-', 'Magazine half way into the well'),
    ('rack-grip-pulled-', 'Rack pulled half way'),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--film', action='append', default=[])
    ap.add_argument('--before')
    ap.add_argument('--after')
    ap.add_argument('--guns', default='', help='comma separated name parts for the before/after pairs')
    ap.add_argument('--hands', action='append', default=[])
    ap.add_argument('--title', default='Ravenfield VR: what was built')
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    media = os.path.join(a.out, 'media')
    os.makedirs(media, exist_ok=True)
    parts = [f'<!doctype html><meta charset="utf-8"><title>{html.escape(a.title)}</title>',
             '<style>body{font-family:system-ui,sans-serif;background:#16181c;color:#e6e6e6;margin:24px}'
             'h2{margin-top:32px}figure{display:inline-block;margin:8px;vertical-align:top}'
             'img{max-width:100%;border:1px solid #333}figcaption{font-size:13px;color:#aaa;max-width:640px}'
             '.pair{display:flex;gap:8px;flex-wrap:wrap}.pair figure{flex:1;min-width:300px}</style>',
             f'<h1>{html.escape(a.title)}</h1>']

    def copy(src):
        name = os.path.basename(src)
        dst = os.path.join(media, name)
        if not os.path.exists(dst):
            shutil.copyfile(src, dst)
        return 'media/' + name

    for d in a.film:
        gifs = sorted(f for f in os.listdir(d) if f.endswith('.gif') and (f.startswith('reload-') or f.endswith('-slow.gif')))
        if not gifs:
            continue
        parts.append(f'<h2>Films: {html.escape(os.path.basename(os.path.normpath(d)))}</h2>')
        for g in gifs:
            parts.append(f'<figure><img src="{copy(os.path.join(d, g))}" loading="lazy"><figcaption>{html.escape(g)}</figcaption></figure>')

    if a.hands:
        parts.append('<h2>Hand poses</h2>')
        for h in a.hands:
            if os.path.exists(h):
                parts.append(f'<figure><img src="{copy(h)}"><figcaption>{html.escape(os.path.basename(h))}</figcaption></figure>')

    if a.before and a.after:
        guns = [g.strip().lower() for g in a.guns.split(',') if g.strip()]
        before = os.listdir(a.before)
        after = set(os.listdir(a.after))
        parts.append('<h2>Before and after</h2>')
        for prefix, label in PAIRS:
            for f in sorted(before):
                if not f.startswith(prefix) or f not in after:
                    continue
                if guns and not any(g in f.lower() for g in guns):
                    continue
                b = shutil.copyfile(os.path.join(a.before, f), os.path.join(media, 'before-' + f))
                c = shutil.copyfile(os.path.join(a.after, f), os.path.join(media, 'after-' + f))
                parts.append(f'<h3>{html.escape(label)}: {html.escape(re.sub(r"^[a-z-]+-[0-9]+-", "", f[:-4]))}</h3><div class="pair">'
                             f'<figure><img src="media/before-{html.escape(f)}"><figcaption>before</figcaption></figure>'
                             f'<figure><img src="media/after-{html.escape(f)}"><figcaption>after</figcaption></figure></div>')

    with open(os.path.join(a.out, 'index.html'), 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(parts))
    print('wrote', os.path.join(a.out, 'index.html'), len(os.listdir(media)), 'media files')


if __name__ == '__main__':
    sys.exit(main())
