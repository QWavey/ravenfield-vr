"""Private methods, properties and fields of the mod that nothing uses (the C# compiler warns about unused private fields only
when never assigned, and never about unused private methods). A private member can only be used in its own type, so its file
is searched, and every file of a partial class. Harmony patch methods (found by their attributes) and Unity's messages are not
listed. Usage: python -I tools/export_checks/unused_private.py [src folder]"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'src')

DECL = re.compile(r'^\s*private\s+(?:static\s+|readonly\s+|unsafe\s+|override\s+)*(?:[\w<>\[\],.? ]+?)\s+(\w+)\s*(\(|=>|\{|;|=)', re.M)
UNITY = {'Awake', 'Start', 'Update', 'LateUpdate', 'FixedUpdate', 'OnEnable', 'OnDisable', 'OnDestroy', 'OnGUI', 'OnPreCull', 'OnPreRender',
         'OnPostRender', 'OnWillRenderObject', 'OnRenderImage', 'OnApplicationQuit', 'OnTriggerEnter', 'OnTriggerStay', 'OnTriggerExit',
         'OnCollisionEnter', 'OnCollisionStay', 'OnCollisionExit', 'OnAudioFilterRead', 'Main'}
HARMONY = {'Prefix', 'Postfix', 'Transpiler', 'Finalizer', 'TargetMethod', 'TargetMethods', 'Prepare', 'Cleanup'}
WORDS = {'get', 'set', 'value', 'return', 'new', 'class', 'struct', 'enum', 'delegate', 'event'}


def strip(text):
    return re.sub(r'//[^\n]*|/\*.*?\*/', '', text, flags=re.S)  # comments do not count as a use


files = sorted(glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True))
texts = {p: open(p, encoding='utf-8', errors='replace').read() for p in files}
partials = {}
for p, t in texts.items():
    for name in re.findall(r'partial\s+class\s+(\w+)', t):
        partials.setdefault(name, []).append(p)

found = []
for path in files:
    text = texts[path]
    code = strip(text)
    own = len(code)  # (a member is listed at its own file only: the other files of its partial class are searched for its uses)
    for name in re.findall(r'partial\s+class\s+(\w+)', text):
        code += '\n'.join(strip(texts[q]) for q in partials[name] if q != path)
    for m in DECL.finditer(code):
        name = m.group(1)
        if m.start() >= own:
            continue
        if name in UNITY or name in HARMONY or name in WORDS:
            continue
        if re.search(r'\[[^\]]*Harmony[^\]]*\]\s*$', code[max(0, m.start() - 200):m.start()]):
            continue  # a Harmony patch method: Harmony finds it by its attribute
        if len(re.findall(r'\b' + re.escape(name) + r'\b', code)) <= 1:
            line = text[:text.find(m.group(0).strip()[:40])].count('\n') + 1
            found.append((os.path.relpath(path, ROOT), line, name, m.group(2)))
for rel, line, name, kind in found:
    print(f'{rel}:{line}: {name} ({"method" if kind == "(" else "member"})')
print(f'{len(found)} unused private member(s)')
