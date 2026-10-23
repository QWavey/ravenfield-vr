"""Read-only: list Boneworks' MonoScripts (class name -> path id) from globalgamemanagers.assets."""
import sys, UnityPy
data = sys.argv[1]
env = UnityPy.load(data + "/globalgamemanagers.assets")
want = {"Socket", "AlignPlug", "MagazineSocket", "MagazinePlug", "Magazine", "Gun", "SlideGrip", "PhysHand", "Grip",
        "CylinderSlideGrip", "PumpSlideGrip", "MagazineData", "VirtualController", "HandGunGrip", "GunGrip"}
n = 0
for obj in env.objects:
    if obj.type.name == "MonoScript":
        s = obj.read()
        n += 1
        name = getattr(s, "m_ClassName", None) or getattr(s, "name", "")
        if name in want:
            print(f"{obj.path_id}\t{getattr(s, 'm_Namespace', '')}.{name}")
print(f"{n} scripts")
