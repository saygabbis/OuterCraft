# WildCraft

Port of [SkyCraft](https://github.com/chasmlol/SkyCraft) to **Outer Wilds**: you play as a Minecraft player on the planets
(Minecraft's physics, inventory, HUD, hand, blocks, lights), while Outer Wilds keeps its solar system, time loop and ship.
Minecraft runs hidden; Outer Wilds draws everything. Shared memory: `Local\SkyCraft_v1` (SkyCraft protocol v10, unchanged).

## Play

`wildcraft\tools\JOGAR.bat` (builds the mod if needed, closes stale Minecraft instances, starts Minecraft hidden and Outer Wilds via OWML).

1. In Outer Wilds, resume your expedition and wait to wake up at the campfire.
2. Once Minecraft is ready its hotbar appears: you are a Minecraft player.

| Key | Does |
|---|---|
| WASD / mouse / Space / Shift, E, 1-9, F5, T, clicks, scroll | Minecraft |
| **O** | Minecraft menu |
| **Esc** | Outer Wilds menu (closes an open Minecraft screen first) |
| **G** | Outer Wilds "interact" (campfire, ship, people) |

In the ship, in conversations or sitting, Outer Wilds is in charge; on foot again, Minecraft takes over.

## How it works
- `PlanetFrame`: Minecraft's flat x/z is an azimuthal map of the planet around a fixed anchor, y is height over the surface (down = toward the planet, like Outer Wilds gravity). Fixed per planet, stored in the planet's own space, so placed blocks stay put as it spins and orbits (`frames.txt` + Minecraft world).
- `CollisionExporter`: ray-casts the terrain around you into triangles + 1/8-block voxels for Minecraft's collision.
- `Bridge`: puppets the player body/camera to Minecraft's interpolated 20 Hz ticks, mouse look, F5, bob, time of day, damage/death both ways.
- `McWorldRender` / `McCompositor`: Minecraft's block meshes (bent onto the planet, torches become real lights) and hand/HUD overlay.
- `GamePatches`: one gate on Outer Wilds' input reads while Minecraft drives.

## Rebuild
```powershell
powershell -ExecutionPolicy Bypass -File wildcraft\tools\install_owml_mod.ps1
```
Logs: `wildcraft\owml\_runtime\OWML\Logs\latest.txt`, `fabric\run\logs\latest.log`.

## Not done yet
Water/swimming in Outer Wilds oceans, combat against Outer Wilds characters, non-spherical bodies (Hollow's Lantern, Brittle Hollow fragments), block shadows.
