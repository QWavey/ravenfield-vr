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
    'WITH SHADOWS': 'AutoTest.RenderingStateCheck raises it with Problem(...)',
    '(TOO FAST': 'AutoTest raises it with Problem(line)',
    '(TOO FEW LENS FLASHES)': 'AutoTest raises the flash count with Problem(...)',
    'LIVE GRENADE WITH THE PIN IN': 'AutoTest raises it as a grenade pin problem',
    'LIVE GRENADE WITH': "AutoTest's own test for that line (l.Contains), not a report",
    'STILL ON ITS BOX': 'AutoTest raises it with Problem(line)',
    'STILL COUNTS AS POINTING': 'AutoTest raises it as a squad problem',
    'THE SLIDE STAYED BACK': 'the pattern has "STAYED BACK after", which the line goes on with',
    'OPEN COVER': "the gun counter's message (FlashCounter) when a box is held by its belt",
    'TRIGGER TO PUMP': "the pump's cue to the player (FlashMessage) on a grip-only pull, no check result",
    '(OVER THE FLASH LIMIT)': 'AutoTest raises the limiter with Problem(...) beside it',
    'LET GO': "AutoTest raises the support pull's failures with Problem(...)",
    'STILL HOLDS': "AutoTest raises the support pull's failures with Problem(...)",
    'STILL ON THE MAGNIFIER': 'AutoTest raises it with Problem(...) (magnifier: letting go left the hand on it)',
    'HANDS WRONG (': 'AutoTest raises a failed pass to the other hand with Problem(...)',
    'THE SLIDE RUNS FORWARD': 'AutoTest raises it with Problem(...) (the animator shows rounds loaded)',
    'STILL OPEN': 'the wrist view and pause menu checks raise it with Problem(...)',
    'COVERED BY': "the pause menu check counts it (bad) and raises Problem(...)",
    'OUT OF SIGHT': "the pause menu check counts it (bad) and raises Problem(...)",
    '(INSIDE OUT)': "the flat self-test's report (WeaponSelfTest), read by eye",
    "THE PLAYER'S": "a diagnostic: whose collider a held body's hardest contact was (BodyImpacts.HardestContact)",
    "ITS OWN SOLDIER'S": "a diagnostic: whose collider a held body's hardest contact was (BodyImpacts.HardestContact)",
    '(STILL SLOWED': "AutoTest's slowed hands check raises it with Problem(line) (slow motion off, the hand not caught up)",
    "THE RESERVE'S MAGAZINE HELD": "AutoTest's hand loading check raises it with Problem(line)",
    'WITH THE RESERVE OUT NOT EMPTY': "AutoTest's hand loading check raises it with Problem(line)",
}
CAPS = re.compile(r"\(?\b[A-Z][A-Z0-9'()]+(?:[ -]\(?[A-Z0-9'()]+\b\)?)+")


def literals(line):
    """The text of every string literal on a line of C#, the ones nested in an interpolated string's holes too: most
    failures are written as `$"...{(bad ? " (NOT TAKEN)" : "")}"`, and a pattern over whole literals ended the outer one at
    the inner quote and never saw them. Text parts are split at the holes; char literals and // comments are skipped."""
    out = []
    n = len(line)

    def char_end(j):  # past a char literal starting at j ('x', '\'', '\u0041')
        k = j + 2 if j + 1 < n and line[j + 1] == '\\' else j + 1
        end = line.find("'", k + 1)
        return n if end < 0 else end + 1

    def string_at(j):  # past a string literal starting at j ($, @ prefixes), its text parts added to out
        interp = verbatim = False
        while j < n and line[j] in '$@':
            interp |= line[j] == '$'
            verbatim |= line[j] == '@'
            j += 1
        j += 1  # the opening quote
        cur = []
        while j < n:
            c = line[j]
            if verbatim and c == '"' and line[j + 1:j + 2] == '"' or not verbatim and c == '\\':
                cur.append(line[j:j + 2])
                j += 2
            elif c == '"':
                j += 1
                break
            elif interp and c in '{}' and line[j + 1:j + 2] == c:
                cur.append(c)
                j += 2
            elif interp and c == '{':
                out.append(''.join(cur))
                cur = []
                j = code(j + 1, hole=True)
            else:
                cur.append(c)
                j += 1
        out.append(''.join(cur))
        return j

    def code(j, hole=False):  # past code (a hole ends at its closing brace), every literal in it read
        depth = 0
        while j < n:
            c = line[j]
            if c == '/' and line[j + 1:j + 2] == '/':
                return n
            if c == "'":
                j = char_end(j)
            elif c == '"' or c in '$@' and '"' in line[j + 1:j + 3] and line[j + 1:j + 3].lstrip('$@').startswith('"'):
                j = string_at(j)
            elif hole and c == '{':
                depth += 1
                j += 1
            elif hole and c == '}':
                if depth == 0:
                    return j + 1
                depth -= 1
                j += 1
            else:
                j += 1
        return j

    code(0)
    return out
ACRONYMS = {'VR', 'HK', 'MG', 'AR', 'AK', 'RPG', 'UI', 'HUD', 'GPU', 'CPU', 'MP', 'SMAW', 'ACOG', 'OK', 'ID', 'LOD', 'RFC', 'USB'}


def failure_pattern():
    text = open(os.path.join(REPO, 'src', 'Core', 'AutoTest.cs'), encoding='utf-8').read()
    m = re.search(r'Regex Failure = new Regex\(@"((?:[^"]|"")*)"\)', text)
    return re.compile(m.group(1).replace('""', '"'))


# The reader's own cases: (a line of C#, the literal texts it must give).
LITERAL_CASES = [
    ('report($"a {(x ? " (NOT ALL OUT: {n} were in)" : "")}; b");', ['a ', ' (NOT ALL OUT: {n} were in)', '', '; b']),
    ("var c = '\"'; s = \"ONE TWO\"; // \"THREE FOUR\"", ['ONE TWO']),
    ('x = @"C:\path ""QUOTED"" END";', ['C:\path ""QUOTED"" END']),
    ('y = $"{{BRACES}} {(a ? $" (INNER {b:0.0} HOLE)" : "")} tail";', ['{BRACES} ', ' (INNER ', ' HOLE)', '', ' tail']),
    ('z = "esc \\"quote\\" here" + "next";', ['esc \\"quote\\" here', 'next']),
]


def main():
    failure = failure_pattern()
    missed, checked = [], 0
    for line, want in LITERAL_CASES:
        got = literals(line)
        if got != want:
            missed.append(f'literal reader: {line!r} gave {got!r}, not {want!r}')
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
                for lit in literals(line):
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
