"""Doc comments stacked on one member: a <summary> block followed straight by another (no member between), the first one left
behind when the member it described was moved or removed. Prints each pair with the member that follows, so the stray block
can be moved to its member or dropped. Usage: python -I tools/export_checks/stacked_docs.py [--brief]"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def blocks(lines, i):
    """The doc block that starts at line i: (first line, last line)."""
    j = i
    while j + 1 < len(lines) and lines[j + 1].strip().startswith('///'):
        j += 1
    return i, j


count = 0
brief = '--brief' in sys.argv
for path in sorted(glob.glob(os.path.join(ROOT, 'src', '**', '*.cs'), recursive=True)):
    lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
    i = 0
    while i < len(lines):
        if lines[i].strip().startswith('/// <summary>') and (i == 0 or not lines[i - 1].strip().startswith('///')):
            start, end = blocks(lines, i)
            text = ' '.join(l.strip()[3:].strip() for l in lines[start:end + 1])
            n = len(re.findall(r'<summary>', text))
            if n > 1:
                count += 1
                member = next((lines[k].strip() for k in range(end + 1, min(end + 6, len(lines))) if lines[k].strip() and not lines[k].strip().startswith('[')), '')
                rel = os.path.relpath(path, ROOT)
                if brief:
                    print(f'{rel}:{start + 1}: {n} summaries on {member[:80]}')
                else:
                    parts = [p.split('</summary>')[0].strip() for p in text.split('<summary>')[1:]]
                    print(f'{rel}:{start + 1}: on  {member[:100]}')
                    for p in parts:
                        print(f'    - {p[:150]}')
            i = end + 1
        else:
            i += 1
print(f'{count} member(s) with stacked summaries')
