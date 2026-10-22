"""Offline: every failure the mod's self-checks write in capitals counts as a problem.

The autotest marks a weapon line as a problem only when its Failure pattern (src/Core/AutoTest.cs) matches it. A check that
words its failure in capitals the pattern does not know writes it into the report's lines and never into its problems: the
magazine keep-out's "PAST THE GUN (WENT THROUGH)" and "THE MAGAZINE WENT INTO THE GUN" were in every report since 2026-10-17
and in none of their problem lists. This reads every string literal in src/, takes each run of two or more capital words,
and fails on one the pattern does not take, unless it is listed below as no failure (a label, a name) or as a failure the
code raises itself with Problem(...).
"""
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Files whose capitals are labels or names, not check results: the gun's counter (AmmoState), magazine-line and donor tables,
# setting descriptions, the squad UI's labels, the fire-mode flash.
LABEL_FILES = {'src/Weapons/AmmoState.cs', 'src/Weapons/MagLineTable.cs', 'src/Weapons/SoundDonors.cs', 'src/Core/VRConfig.cs',
               'src/UI/SquadPointing.cs', 'src/Weapons/WeaponSystem.cs'}
# Phrases that are no failure, or a failure the code raises itself with Problem(...) next to it.
NOT_BY_PATTERN = {
    'THE EMPTY POSE': 'a diagnostic: which animator state the gun is in',
    '(TOO FAST': 'AutoTest raises it with Problem(line)',
    '(TOO FEW LENS FLASHES)': 'AutoTest raises the flash count with Problem(...)',
    'LIVE GRENADE WITH': 'AutoTest raises it as a grenade pin problem',
    'STILL ON ITS BOX': 'AutoTest raises it with Problem(line)',
    'STILL COUNTS AS POINTING': 'AutoTest raises it as a squad problem',
    'THE SLIDE STAYED BACK': 'the pattern has "STAYED BACK after", which the line goes on with',
    'OPEN COVER': "the gun counter's message (FlashCounter) when a box is held by its belt",
}
CAPS = re.compile(r"\(?\b[A-Z][A-Z0-9'()]+(?:[ -]\(?[A-Z0-9'()]+\b\)?)+")
LITERAL = re.compile(r'\$?@?"((?:[^"\\\n]|\\.)*)"')
ACRONYMS = {'VR', 'HK', 'MG', 'AR', 'AK', 'RPG', 'UI', 'HUD', 'GPU', 'CPU', 'MP', 'SMAW', 'ACOG', 'OK', 'ID', 'LOD', 'RFC', 'USB'}


def failure_pattern():
    text = open(os.path.join(REPO, 'src', 'Core', 'AutoTest.cs'), encoding='utf-8').read()
    m = re.search(r'Regex Failure = new Regex\(@"((?:[^"]|"")*)"\)', text)
    return re.compile(m.group(1).replace('""', '"'))


def main():
    failure = failure_pattern()
    missed, checked = [], 0
    for folder, _, files in os.walk(os.path.join(REPO, 'src')):
        for f in sorted(files):
            if not f.endswith('.cs'):
                continue
            path = os.path.join(folder, f)
            rel = os.path.relpath(path, REPO).replace('\\', '/')
            if rel in LABEL_FILES:
                continue
            for n, line in enumerate(open(path, encoding='utf-8'), 1):
                if line.strip().startswith('//') or 'Regex Failure' in line:
                    continue  # (comments, and the pattern's own words)
                for lit in LITERAL.findall(line):
                    for phrase in CAPS.findall(lit):
                        words = re.findall(r'[A-Z]{2,}', phrase)
                        if len(words) < 2 or all(w in ACRONYMS for w in words):
                            continue
                        checked += 1
                        if not failure.search(phrase) and phrase.strip() not in NOT_BY_PATTERN:
                            missed.append(f'{rel}:{n}: {phrase.strip()!r}')
    print(f'{checked} capital phrases in the self-checks, {len(missed)} not counted as problems')
    for m in missed:
        print('  ' + m)
    # The pattern itself, on the lines that were missed before (from real reports).
    for line in ('sideways slowly: 0 frame(s) drawn inside the gun, ended 24.6 cm PAST THE GUN (WENT THROUGH)',
                 'magazine keep-out: THE MAGAZINE WENT INTO THE GUN', 'PICKED-UP MAGAZINE LOST ROUNDS (28/30)',
                 'locked empty: the grab point 4.1 cm from the drawn charging handle (THE HANDLE IS OUT OF THE GRAB)'):
        if not failure.search(line):
            missed.append('pattern misses: ' + line)
            print('  pattern misses: ' + line)
    for line in ('sideways slowly: ended 3.0 cm on its side', 'dropped magazine picked up and seated, 30 rounds kept',
                 'past the gun, but it started 2.0 cm in (not a crossing)'):
        if failure.search(line):
            missed.append('pattern takes a pass: ' + line)
            print('  pattern takes a pass: ' + line)
    return 1 if missed else 0


if __name__ == '__main__':
    sys.exit(main())
