"""Every clip GameSounds names is one of the game's, and each cut lies inside its clip (tools/tests/game_clips.txt: name and
length of the clips GameSounds may use, read from the game's resources.assets with UnityPy). Also: no sound is synthesised."""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))


def main():
    with open(os.path.join(ROOT, "src", "Weapons", "GameSounds.cs"), encoding="utf-8") as f:
        source = f.read()
    known = {}
    with open(os.path.join(HERE, "game_clips.txt"), encoding="utf-8") as f:
        for line in f:
            if line.strip():
                name, length = line.rstrip("\n").split("\t")
                known[name] = float(length)

    cuts = re.findall(r'new Cut\("([^"]+)"((?:,\s*[-0-9.]+f)*)\)', source)
    failures = []
    if len(cuts) < 25:
        failures.append(f"only {len(cuts)} cuts found in GameSounds.cs: the pattern no longer reads it")
    for name, args in cuts:
        numbers = [float(x) for x in re.findall(r"[-0-9.]+(?=f)", args)]
        start = numbers[0] if len(numbers) > 0 else 0.0
        end = numbers[1] if len(numbers) > 1 else 0.0
        if name not in known:
            failures.append(f"{name}: not one of the game's clips")
            continue
        length = known[name]
        stop = end if end > 0.0 else length
        if not 0.0 <= start < stop <= length + 0.02:
            failures.append(f"{name}: cut {start}-{stop} s is outside its {length} s")

    # (The whole mod: a clip made in code is a sound made by the mod.)
    made = []
    for folder, _, files in os.walk(os.path.join(ROOT, "src")):
        for name in files:
            if name.endswith(".cs"):
                with open(os.path.join(folder, name), encoding="utf-8") as f:
                    if "AudioClip.Create" in f.read():
                        made.append(name)
    if made:
        failures.append("AudioClip.Create in " + ", ".join(made) + ": the mod's sounds are the game's only")

    for failure in failures:
        print("FAIL", failure)
    print(f"game clips: {len(cuts)} cuts of {len(set(c[0] for c in cuts))} clips checked, {len(failures)} failures")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
