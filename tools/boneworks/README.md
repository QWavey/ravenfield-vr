# Reading Boneworks' files (study only)

Boneworks is IL2CPP (no C# assemblies): these read its files **read-only** to learn how its mechanics work, never to
copy code. Nothing here writes into the game folder, and no game data is kept in the repository. Findings: Tasks.md 244
and 334.

1. `python -I il2cpp_meta.py <BONEWORKS_Data>/il2cpp_data/Metadata/global-metadata.dat <out>` - every type's fields,
   properties and methods (by name, v24.1 layout) per assembly, the code's string literals and constant values.
2. `python -I list_scripts.py <BONEWORKS_Data>` - the MonoScript path ids of the classes of interest (edit `want`).
3. `python -I scan_scripts.py <BONEWORKS_Data> found.pkl` - every MonoBehaviour of those scripts in every level and asset
   file, raw bytes kept (up to 40 each; fill `SCRIPTS` from step 2). Needs UnityPy.
4. `python -I decode_words.py found.pkl <Class> <n>` - each one's fields as 4-byte words (ints, floats, PPtrs), to lay
   out by the field order step 1 gives (the type trees are stripped: field types come from their names and values).
