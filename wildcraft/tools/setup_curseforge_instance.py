#!/usr/bin/env python3
"""Create a CurseForge Minecraft instance for WildCraft / SkyCraft Fabric 26.3."""

from __future__ import annotations

import json
import shutil
import ssl
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

CF_ROOT = Path.home() / "curseforge" / "minecraft"
INSTANCES = CF_ROOT / "Instances"
VERSIONS = CF_ROOT / "Install" / "versions"
INSTANCE_NAME = "WildCraft"
JAVA25 = Path(r"C:\Program Files\Eclipse Adoptium\jdk-25.0.3.9-hotspot\bin\java.exe")
REPO = Path(__file__).resolve().parents[2]
FABRIC_OUT = REPO / "fabric" / "build" / "libs"

LOADER = "0.19.5"
MC = "26.3"
FAPI_VER = "0.161.0+26.3"

CTX = ssl.create_default_context()


def get(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "WildCraft-setup/0.1"})
    with urllib.request.urlopen(req, context=CTX, timeout=120) as r:
        return r.read()


def get_json(url: str):
    return json.loads(get(url).decode("utf-8"))


def download(url: str, dest: Path):
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() and dest.stat().st_size > 0:
        print(f"  exists: {dest.name}")
        return
    print(f"  download: {dest.name}")
    dest.write_bytes(get(url))


def ensure_mc_and_fabric():
    # Vanilla 26.3
    man = get_json("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")
    entry = next(v for v in man["versions"] if v["id"] == MC)
    ver = get_json(entry["url"])
    mc_dir = VERSIONS / MC
    mc_dir.mkdir(parents=True, exist_ok=True)
    (mc_dir / f"{MC}.json").write_text(json.dumps(ver, indent=2), encoding="utf-8")
    client = ver["downloads"]["client"]
    download(client["url"], mc_dir / f"{MC}.jar")

    # Fabric profile
    profile = get_json(
        f"https://meta.fabricmc.net/v2/versions/loader/{MC}/{LOADER}/profile/json"
    )
    fid = f"fabric-loader-{LOADER}-{MC}"
    fdir = VERSIONS / fid
    fdir.mkdir(parents=True, exist_ok=True)
    # CurseForge-style compact version json (libraries with url field)
    cf_libs = []
    for lib in profile.get("libraries", []):
        name = lib["name"]
        url = lib.get("url") or "https://maven.fabricmc.net/"
        # Prefer forgecdn mirror like other CF fabric profiles
        if "fabricmc.net" in url:
            url = "https://fabricmc.forgecdn.net/"
        cf_libs.append(
            {
                "name": name,
                "url": url,
                "serverreq": None,
                "clientreq": None,
                "natives": None,
                "extract": None,
                "rules": None,
            }
        )
    now = datetime.now().astimezone().isoformat(timespec="seconds")
    cf_json = {
        "assetIndex": None,
        "id": fid,
        "time": now,
        "releaseTime": now,
        "type": "release",
        "minecraftArguments": None,
        "minimumLauncherVersion": 0,
        "assets": None,
        "inheritsFrom": MC,
        "jar": None,
        "logging": None,
        "libraries": cf_libs,
        "mainClass": profile["mainClass"],
        "arguments": profile.get("arguments")
        or {"game": [], "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "]},
    }
    (fdir / f"{fid}.json").write_text(json.dumps(cf_json), encoding="utf-8")
    # CF copies the client jar next to the fabric profile in some installs
    shutil.copy2(mc_dir / f"{MC}.jar", fdir / f"{fid}.jar")
    return fid, cf_json, ver


def modrinth_versions(slug: str):
    qv = urllib.parse.quote(json.dumps([MC]))
    ql = urllib.parse.quote(json.dumps(["fabric"]))
    url = f"https://api.modrinth.com/v2/project/{slug}/version?game_versions={qv}&loaders={ql}"
    return get_json(url)


def pick_mod(slug: str, prefer_contains: str | None = None):
    vers = modrinth_versions(slug)
    if not vers:
        raise RuntimeError(f"No Modrinth versions for {slug} on {MC}")
    if prefer_contains:
        for v in vers:
            if prefer_contains in v.get("version_number", ""):
                return v
    return vers[0]


def find_skycraft_jar() -> Path:
    patterns = ("skycraft-fabric-*.jar", "skycraft-*.jar")
    candidates: list[Path] = []
    for pat in patterns:
        candidates.extend(FABRIC_OUT.glob(pat))
    candidates = sorted(set(candidates), key=lambda p: p.stat().st_mtime, reverse=True)
    for c in candidates:
        n = c.name.lower()
        if "sources" in n or "-dev" in n or "javadoc" in n:
            continue
        return c
    for p in [
        REPO / "skycraft-fabric-0.1.0.jar",
        REPO / "skycraft-0.1.0.jar",
        *REPO.glob("**/skycraft-fabric-0.1.0.jar"),
        *REPO.glob("**/skycraft-0.1.0.jar"),
    ]:
        if p.is_file() and "sources" not in p.name.lower():
            return p
    raise FileNotFoundError(
        f"SkyCraft fabric jar not found. Build with: cd fabric && gradlew build -x test (JAVA_HOME=JDK25). Looked in {FABRIC_OUT}"
    )


def build_instance(fid: str, cf_loader_json: dict):
    inst = INSTANCES / INSTANCE_NAME
    mods = inst / "mods"
    mods.mkdir(parents=True, exist_ok=True)
    for d in ("saves", "resourcepacks", "shaderpacks", "config", "profileImage"):
        (inst / d).mkdir(exist_ok=True)

    # Mods
    fapi = pick_mod("fabric-api", FAPI_VER)
    f = fapi["files"][0]
    download(f["url"], mods / f["filename"])

    try:
        e4 = pick_mod("e4mc")
        ef = e4["files"][0]
        download(ef["url"], mods / ef["filename"])
    except Exception as e:
        print(f"  warn: e4mc skipped ({e})")

    sky = find_skycraft_jar()
    dest_sky = mods / "skycraft-fabric-0.1.0.jar"
    shutil.copy2(sky, dest_sky)
    print(f"  copied: {dest_sky.name} (from {sky.name})")

    # JVM args — visible window for WildCraft; link name explicit
    jvm = r"--enable-native-access=ALL-UNNAMED -Dskycraft.link=Local\SkyCraft_v1 -Dskycraft.hostName=Outer Wilds -Dfile.encoding=UTF-8"

    guid = str(uuid.uuid4())
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    version_json_str = json.dumps(cf_loader_json, separators=(",", ":"))

    instance = {
        "baseModLoader": {
            "forgeVersion": LOADER,
            "name": fid,
            "type": 4,  # Fabric in modern CurseForge
            "downloadUrl": "",
            "filename": f"{fid}.jar",
            "installMethod": 4,
            "latest": True,
            "recommended": True,
            "versionJson": version_json_str,
            "librariesInstallLocation": None,
            "minecraftVersion": MC,
        },
        "isUnlocked": True,
        "gameVersion": MC,
        "gameVersionFlavor": None,
        "gameVersionTypeId": None,
        "name": INSTANCE_NAME,
        "guid": guid,
        "installPath": str(inst),
        "installDate": now,
        "lastPlayed": None,
        "playedCount": 0,
        "isMemoryOverride": True,
        "allocatedMemory": 4096,
        "memoryAllocatedType": 1,
        "javaArgsOverride": jvm,
        "isValid": True,
        "isEnabled": True,
        "isVanilla": False,
        "preferenceAlternateFile": False,
        "preferenceAutoInstallUpdates": False,
        "preferenceDeleteOrphanedDependencies": False,
        "preferenceDeleteSavedVariables": False,
        "preferenceReleaseType": None,
        "preferenceModdingFolderPath": None,
        "profileImagePath": None,
        "projectID": 0,
        "fileID": 0,
        "fileDate": None,
        "customAuthor": "WildCraft",
        "modpackOverrides": None,
        "installedAddons": [],
        "installedGamePrerequisites": [],
        "installedModpack": None,
        "manifest": None,
        "cachedScans": [],
        "lastPreviousMatchUpdate": None,
        "lastRefreshAttempt": None,
        "gameTypeID": 432,
        "groupId": None,
        "syncProfile": None,
        # Custom Java 25 — CF may ignore unknown keys; also write companion note
        "javaPathOverride": str(JAVA25),
        "customJavaPath": str(JAVA25),
    }

    (inst / "minecraftinstance.json").write_text(
        json.dumps(instance, indent=2), encoding="utf-8"
    )

    # options hints
    (inst / "WILDCRAFT.txt").write_text(
        "\n".join(
            [
                "WildCraft / SkyCraft Fabric profile",
                f"Minecraft {MC} + Fabric Loader {LOADER}",
                f"Java 25 required: {JAVA25}",
                "",
                "In CurseForge: open this profile → Settings → Java →",
                "  use Additional Arguments already set, and point Java to JDK 25 if the",
                "  launcher still picks Java 21.",
                "",
                "Launch Outer Wilds (OWML + WildCraft) OR python wildcraft/host/fake_outerwilds.py",
                "THEN play this instance.",
                "",
            ]
        ),
        encoding="utf-8",
    )
    print(f"Instance ready: {inst}")
    return inst


def main():
    print("Setting up CurseForge WildCraft instance...")
    INSTANCES.mkdir(parents=True, exist_ok=True)
    fid, cf_json, _ = ensure_mc_and_fabric()
    print(f"Version profile: {fid}")
    build_instance(fid, cf_json)
    print("Done. Restart / refresh CurseForge if the profile does not appear.")


if __name__ == "__main__":
    main()
