#!/usr/bin/env python3
"""Ensure WildCraft appears in CurseForge's MinecraftGameInstance.json registry."""

from __future__ import annotations

import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

INST = Path.home() / "curseforge" / "minecraft" / "Instances" / "WildCraft" / "minecraftinstance.json"
REG = Path.home() / "AppData" / "Roaming" / "CurseForge" / "agent" / "GameInstances" / "MinecraftGameInstance.json"


def main():
    inst = json.loads(INST.read_text(encoding="utf-8"))
    if REG.exists():
        shutil.copy2(REG, REG.with_suffix(".json.bak-wildcraft"))
        data = json.loads(REG.read_text(encoding="utf-8"))
    else:
        data = []
    # Drop prior WildCraft entries
    data = [i for i in data if i.get("name") != "WildCraft" and i.get("installPath") != inst.get("installPath")]
    data.append(inst)
    REG.parent.mkdir(parents=True, exist_ok=True)
    REG.write_text(json.dumps(data, separators=(",", ":")), encoding="utf-8")
    print(f"Registered WildCraft in {REG} ({len(data)} instances)")


if __name__ == "__main__":
    main()
