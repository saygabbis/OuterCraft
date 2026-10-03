# WildCraft = SkyCraft for Outer Wilds

Follows the official [SkyCraft README](../../README.md), [`docs/DESIGN.md`](../../docs/DESIGN.md) and [`protocol/skycraft_protocol.h`](../../protocol/skycraft_protocol.h). Same architecture; host is Outer Wilds (OWML) instead of Skyrim (SKSE).

## Core principle (DESIGN §1)

Neither game is rewritten. Minecraft keeps physics, inventory, blocks, combat, HUD. Outer Wilds keeps planets, time loop, ship, NPCs, saves. They only **translate** over `Local\SkyCraft_v1`.

## Mapping DESIGN → WildCraft

| DESIGN piece | SkyCraft (Skyrim) | WildCraft (Outer Wilds) |
|---|---|---|
| §3 shared memory | SKSE Link | `WildCraft.Core.HostLink` |
| §5 CollisionField | Havok export | `CollisionProbe` + flat seed |
| §6 PlayerPuppet | disable controls + set pose | LockMovement + planet-relative SetPosition + **match planet velocity** |
| §6 CameraDriver | hook PlayerCamera::Update | `CameraDriver` LateUpdate + Harmony swallow `UpdateInput` |
| §7 InputBridge | DI events → SDL | Unity `Input` → SDL scancodes in protocol |
| §7 routing | Esc=Skyrim, O=MC pause | Esc=OW pause, O=MC pause |
| §9 overlay hand/HUD | DXGI Present | `McCompositor` (CPU blit; GPU interop later) |
| §9 world blocks | WorldRender.cpp depth | `McWorldRender` (opt-in `DrawMinecraftBlocks`) |
| §8 combat proxies | Skyrim actors | not yet (OW fauna later) |
| §9 depth+shadows | Skyrim depth buffer | **not yet** — OW lights the scene; MC overlay is on top |

## Shadows / lighting

SkyCraft draws MC blocks **inside** Skyrim’s frame with Skyrim sun/shadows (depth-tested). WildCraft does not have that Present/depth hook yet. Today: Outer Wilds lights its own world; MC hand/HUD are composited on top. Full block lighting/shadows in the OW frame needs the §9 GPU path.

## Controls (same idea as README “Controls”)

| Key | Owner |
|---|---|
| WASD, mouse, Space, Shift, E, 1–9, click, scroll | Minecraft |
| **O** | Minecraft pause |
| **Esc** | Outer Wilds pause (or closes MC screen if one is open) |
| Ship / flight console | Outer Wilds (bridge pauses) |

## Honest status

Playable target: walk on a planet with MC physics look/inventory without impact-death or camera fight. Full README “What works” parity (blocks in-frame with shadows, combat, water, multiplayer) is the same multi-phase work DESIGN §12 describes for Skyrim — ported piece by piece, not reinvented.
