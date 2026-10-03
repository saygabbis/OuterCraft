import clr  # noqa: F401
import sys
from pathlib import Path

# Use pythonnet if available; else subprocess with a tiny C# program via dotnet script
managed = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Outer Wilds\OuterWilds_Data\Managed\Assembly-CSharp.dll")
print("exists", managed.exists(), managed)

try:
    import System
    from System.Reflection import Assembly, BindingFlags
    asm = Assembly.LoadFrom(str(managed))
except Exception as e:
    print("pythonnet failed", e)
    # fallback: monodis-like via strings
    data = managed.read_bytes()
    # crude ASCII string extract for type names
    import re
    text = re.findall(rb"[A-Za-z_][A-Za-z0-9_]{3,60}", data)
    interesting = [t.decode() for t in text if any(k in t.decode() for k in (
        "PlayerCharacter", "PlayerCamera", "PlayerBody", "OWRigid", "Jetpack", "PauseMenu", "OWInput"
    ))]
    for s in sorted(set(interesting))[:80]:
        print(s)
    sys.exit(0)

flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
for n in ["PlayerCharacterController", "PlayerCameraController", "PlayerBody", "OWRigidbody", "JetpackThrusterController"]:
    t = asm.GetType(n)
    if t is None:
        matches = [x for x in asm.GetTypes() if n in x.Name]
        print("missing", n, "matches", [m.FullName for m in matches[:5]])
        continue
    print("===", t.FullName, "===")
    for m in t.GetMethods(flags):
        if any(k in m.Name for k in ("Update", "Fixed", "Enable", "Disable", "Move", "Look", "Input")):
            print(" ", m.Name)
