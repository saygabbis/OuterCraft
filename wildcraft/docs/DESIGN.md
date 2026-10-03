# WildCraft — Design (MVP)

> Play Outer Wilds as a Minecraft player: MC movement, inventory, HUD and blocks inside Outer Wilds.

Status: MVP scaffold · 2026-10-03

## Principle (same as SkyCraft)

Neither game is rewritten. Minecraft runs physics/inventory/blocks. Outer Wilds runs the solar system.
A host mod and the existing SkyCraft Fabric mod only **translate** over shared memory.

**Protocol reuse:** MVP speaks `Local\SkyCraft_v1` (protocol v10). Coordinates on the wire are always Minecraft space (Y-up, blocks). The host converts Outer Wilds ↔ MC.

## Components

```
┌── OuterWilds.exe (or FakeHost) ──┐     ┌── javaw.exe (Minecraft + SkyCraft Fabric) ──┐
│  WildCraft host                  │◄───►│  fabric/ (unchanged protocol)               │
│  - create shared mapping         │ shm │  - void mirror world                        │
│  - export collision near player  │     │  - MC physics / inventory / HUD             │
│  - puppet player + camera        │     │  - block meshes + overlay                   │
│  - forward input                 │     │                                             │
└──────────────────────────────────┘     └─────────────────────────────────────────────┘
```

| Piece | Path | Status |
|---|---|---|
| Protocol | `protocol/skycraft_protocol.h` (reuse) | Done |
| Fabric guest | `fabric/` | Reuse as-is |
| Fake host (playable without OW) | `wildcraft/host/fake_outerwilds.py` | MVP |
| Real host | `wildcraft/owml/` | Scaffold (needs Outer Wilds + OWML + .NET SDK) |

## MVP scope (functional)

1. Host creates the mapping and heartbeats.
2. Flat collision patch (stand-in for Timber Hearth ground).
3. Host forwards real keyboard/mouse → Minecraft input ring.
4. Host reads `McState` (player pos / look) — real OW would teleport the player body here.
5. Overlay PNG dump on demand (F12) so HUD path is proven.

Out of MVP: planet frames, ship/jetpack takeover, depth-composited blocks in Unity, time-loop save sync.

## Coordinate mapping (MVP)

| Outer Wilds (Unity, Y-up, metres) | Minecraft |
|---|---|
| 1 m | 1 block |
| local patch origin on a body | large MC offset `(200000, 80, 0)` so it never collides with Skyrim test areas |

Later: body-local frames, floating origin, gravity direction injection.

## Phases

| # | Done when |
|---|---|
| 0 | FakeHost + Fabric handshake; walk on flat floor with MC physics |
| 1 | OWML mod loads, same handshake inside Outer Wilds title/system |
| 2 | Puppet player on Timber Hearth; camera follow; basic mesh colliders |
| 3 | Overlay HUD in OW window; ship/suit = takeover (pause MC) |
| 4 | Block meshes drawn in Unity; depth test; time-loop snapshot |

## Risks

- No Outer Wilds install on this machine → ship FakeHost first.
- Unity render interop >> SKSE D3D11 path; defer depth composite.
- Moving bodies need local frames; flat patch only until phase 2.
