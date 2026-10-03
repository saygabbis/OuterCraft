# Guia de portabilidade: Minecraft dentro de QUALQUER jogo (prompt para IA)

> Este arquivo é escrito para ser **colado inteiro em qualquer IA de código** (Claude Code, Cursor, Codex, Antigravity, Copilot, etc.) junto com a pasta do projeto. Ele contém o objetivo, a arquitetura, o contrato (protocolo), o passo a passo, as armadilhas que já custaram horas, e como verificar cada etapa. Nada aqui depende de uma IA específica.
>
> Base: SkyCraft (Skyrim) e WildCraft (Outer Wilds). O lado Minecraft (`fabric/`) e o protocolo (`protocol/`) **não mudam** entre jogos. Só o **host** (o mod do jogo alvo) é reescrito.

---

## 0. Instruções para a IA (leia primeiro)

1. Você vai criar um **mod host** para um jogo alvo `<JOGO>` que fale o protocolo `Local\SkyCraft_v1` (versão 10) definido em `protocol/skycraft_protocol.h`. O Minecraft (Fabric 26.3, Java 25) roda **escondido** e **não é reescrito**. O jogo alvo desenha tudo.
2. Princípio central (nunca violar): **nenhum dos dois jogos é reescrito**. O Minecraft mantém física, inventário, blocos, combate, HUD. O jogo alvo mantém mundo, NPCs, missões, saves. O host só **traduz** pela memória compartilhada. Se você se pegar reimplementando uma mecânica do Minecraft no host, o design está errado.
3. Trabalhe em **fases** (seção 6). Cada fase termina em algo que dá para **ver rodando**. Não avance sem verificar.
4. **Teste de verdade.** Compilar não prova nada. Rode o jogo, capture a tela, leia os logs (seção 8). Nunca declare "funciona" sem ter visto.
5. Não invente caminhos, nomes de classe ou offsets. Descubra pelo jogo real (reflexão, descompilação, logs) e pelo código existente neste repositório (seção 11).
6. Se algo for impossível ou incerto, **diga** e proponha alternativa. Não finja.

---

## 1. Arquitetura

```
┌──────── Jogo alvo (processo) ────────┐        ┌──────── javaw.exe (Minecraft 26.3 + Fabric) ───────┐
│ Mod HOST (você escreve)              │        │ Mod Fabric SkyCraft (NÃO muda)                      │
│  cria o mapeamento de memória        │◄─shm──►│  mundo "espelho" vazio (void)                       │
│  exporta colisão do mundo do jogo    │        │  física/inventário/HUD/blocos do Minecraft          │
│  encaminha teclado/mouse             │        │  envia: estado do jogador, meshes de blocos,        │
│  move o corpo/câmera do jogo alvo    │        │  atlas, overlay (HUD+mão), entidades, eventos       │
│  desenha blocos + overlay            │        │                                                     │
└──────────────────────────────────────┘        └─────────────────────────────────────────────────────┘
```

- Memória compartilhada nomeada `Local\SkyCraft_v1`. **O host cria**, o Minecraft abre. Layout byte a byte em `protocol/skycraft_protocol.h` (espelho Java em `fabric/src/main/java/dev/skycraft/link/Proto.java`, espelho C# em `wildcraft/owml/WildCraft.Core/Proto.cs`). Little-endian, structs de tamanho fixo.
- **Coordenadas no fio são sempre espaço Minecraft**: X leste, Y cima, Z sul, 1 bloco = 1 unidade. O host converte entre o espaço do jogo alvo e o do Minecraft.
- O Minecraft é **autoritativo** para posição e física do jogador. O host é um "fantoche": move o corpo do jogador do jogo alvo para onde o Minecraft diz.

---

## 2. Pré-requisitos e o que escolher no jogo alvo

Antes de escrever código, responda (descubra, não suponha):

| Pergunta | Por quê |
|---|---|
| Qual engine? Linguagem do mod? Como se injeta código (SKSE, OWML/BepInEx, MelonLoader, ASI/DLL hook, Lua, plugin oficial)? | Define o toolchain do host. Prefira o framework de mods mais comum do jogo. |
| Dá para ler/escrever memória compartilhada (`CreateFileMapping`/`MemoryMappedFile`) no código do mod? | Obrigatório. Se o runtime bloquear P/Invoke, use caminho completo (`C:\Windows\System32\kernel32.dll`): no Unity/Mono, `DllImport("user32.dll")` falhou com `DllNotFoundException ...\Plugins\x86_64\user32.dll`. |
| Como mover o jogador por código (teleporte/posição/rotação) sem disparar eventos pesados? | Núcleo do "fantoche". Veja armadilha A1. |
| Como ler a geometria do mundo (raycast físico, malha de colisão)? | Alimenta a colisão do Minecraft. Raycast em grade é suficiente (estágio A). |
| Como desenhar malhas 3D com textura no frame do jogo, com depth e luz do jogo? | Para blocos. Descubra **quais shaders existem na build** (armadilha D1). |
| Como desenhar um overlay 2D por cima de tudo (HUD)? | IMGUI/Canvas/hook de Present. Overlay chega como textura RGBA **premultiplicada**. |
| Como ler teclado/mouse **sem** usar a API que o jogo bloqueia? | No OW, `UnityEngine.Input` é proibido (Input System novo). Use a API que o jogo realmente usa. |
| Como bloquear o input do próprio jogo enquanto o Minecraft controla? | Um "portão" no ponto de leitura de input (Harmony/hook), não várias correções soltas. |
| A gravidade/mundo é plano ou esférico? | Se esférico, precisa de projeção (seção 5). Se plano, o mapeamento é afim simples. |
| O jogo tem estados em que o jogador não anda (veículo, diálogo, cutscene, água, mini-games)? | Cada um é um **handoff** (seção 5.9). |

Ambiente que já funciona (Windows): JDK 25 (Eclipse Adoptium), Gradle wrapper do `fabric/`, .NET SDK para mods C#, Python opcional só para os fakes de teste.

---

## 3. O que NÃO muda (lado Minecraft)

Pasta `fabric/` (mod Fabric). Propriedades JVM que configuram o host:

- `-Dskycraft.link=Local\SkyCraft_v1` nome do mapeamento.
- `-Dskycraft.hostName=<Nome do jogo>` só para logs/Discord.
- `-Dskycraft.startHidden=true`, `-Dskycraft.showWindow=false` janela do MC escondida.
- `-Dskycraft.quitWithSkyrim=false` não fechar quando o "host pid" some (útil em desenvolvimento). O campo do protocolo ainda se chama `skyrimPid`.
- `-Dskycraft.autoRespawn=true` respawn automático quando o host teleporta um jogador morto.

Edite `fabric/build.gradle` (`loom.runs.client.vmArgs`) para o seu jogo. O mod abre o mundo "SkyCraft" sozinho quando o link sobe e o jogador aparece quando o host manda `kSkyInGame` + `teleportSeq`.

Já existem no lado Minecraft (não reimplemente no host): física, colisão contra triângulos/voxels vindos do host, mundo vazio persistente, geração das meshes de bloco, atlas, overlay de HUD/mão, avatar do jogador, luzes de bloco, cracks/itens/flechas (tabela `WorldEntities`), eventos (morte, explosão), multiplayer.

---

## 4. Contrato do protocolo (resumo operacional)

Leia `protocol/skycraft_protocol.h` inteiro; abaixo o que o host **faz** em cada região:

| Região | Direção | O host... |
|---|---|---|
| Header @0 | ambos | grava magic `0x43594B53` ("SKYC"), versão 10, seu PID em `skyrimPid`; **atualiza `skyrimHeartbeatMs` a cada frame** (`GetTickCount64`). Considere o MC vivo se `mcHeartbeatMs` mudou nos últimos ~8 s. |
| SkyState @0x100 | host→MC | seqlock (seq ímpar = escrevendo; incrementa de 2 em 2 **a cada frame**: o MC espera esse avanço para sincronizar quadros). Campos: flags (`InGame`, `MenuOpen`, `Loading`, e **bit 3 = SoftTeleport**, extensão deste projeto), worldId, `collisionEpoch`, posição de spawn (coords MC), **yaw/pitch autoritativos** (graus MC), `teleportSeq`, viewport, gameHour. |
| McState @0x200 | MC→host | ler com seqlock (repetir se seq ímpar ou mudou). Contém posição interpolada, olhar, flags (InWorld, ScreenOpen, OnGround, Sneaking, Sprinting, Dead, Swimming, Flying), `teleportAck`, FOV, bob, **ticks de 20 Hz** (`tickQpc`, `prev*`, `cur*`, `tickEyeO/tickEye`, `tickMs`...), `cameraMode`/`cameraDistance` (F5) e, no offset 0x4C, **luz do item na mão** (extensão deste projeto, nível 0-15). |
| Overlay @0x300 | MC→host | triple buffer RGBA8, `bottomUp=1`, **alpha premultiplicado**. Troque `state` conforme o comentário do header. Desenhe por cima de tudo. |
| Input ring @0x1000 | host→MC | eventos de 16 bytes: tecla (scancode **SDL**), botão, scroll (120/notch), cursor (pixels do overlay), texto, `ReleaseAll`, `Hurt` (dano do jogo→MC), `OpenMenu`, **`Velocity`=9** (extensão: a,b,c = blocos/tick ×10000). **Nunca estoure o anel**: compare `head-tail`, descarte se ≥ ~4000. |
| Collision ring @0x20000 | host→MC | mensagens `{type,payloadBytes}` alinhadas a 8: `Clear(epoch)`, `Tris` (triângulos exatos, para o colisor suave do jogador), `Region` (voxels 8×8×8 de 1/8 de bloco). Enviar **`Tris` antes de `Region`** por região 8³. Nunca lapear o consumidor (cheque `tail`). |
| Render ring @… | MC→host | `Atlas`, `AtlasRegion` (animações), `Section` (mesh de um cubo 16³, 0 vértices = remover), `Lights`, `Texture`, `Avatar`, `Scene`, `Clear`, `Ragdoll`, `Solids`. Drene todo frame, **mesmo quando não desenha**, senão o anel enche. |
| World entities @0x1C000 | MC→host | tabela com seqlock: flechas, itens soltos, blocos soltos, rachaduras, contorno do bloco, sombras. O host **desenha** (o MC não manda mesh para isso). |
| Event ring @0x17000 | MC→host | `PlayerDied`, `Explosion`, `HitActor`... Consuma sempre. |
| Actor table @0x12000 | host→MC | (opcional, fase de combate) NPCs do jogo como entidades-proxy invisíveis e atacáveis. |

Convenções de rotação Minecraft: yaw 0 olha +Z (sul), yaw 90 olha −X (oeste); pitch positivo olha para baixo. Direção de olhar = `(-sin(yaw)·cos(pitch), -sin(pitch), cos(yaw)·cos(pitch))`.

Fórmula do mouse (idêntica ao Minecraft): `d = sens*0.6+0.2; graus = pixels * d³ * 8 * 0.15`, `sens` vem de `McState.sensitivity`.

---

## 5. O host: componentes e como implementar cada um

Implementação de referência: `wildcraft/owml/` (C# para Unity/OWML). Para outro jogo, mantenha **a mesma divisão de responsabilidades**, trocando só as chamadas da engine.

### 5.1 Link (memória compartilhada)
- `HostLink`: cria `MemoryMappedFile.CreateOrOpen(name, size)` com o tamanho total (`kMappingBytes`), zera cabeçalho/regiões de controle, escreve magic/versão/pid. Métodos: `Heartbeat`, `MinecraftAlive`, `WriteSky`, `ReadMc` (seqlock), `PushInput`, `WriteCollision`, `TryAcquireOverlay`, `DrainRender`, `DrainEvents`, `ReadWorldEntities`.
- Se o jogo não for .NET, reescreva em C++/Rust seguindo `skse/src/Link.cpp` como referência.

### 5.2 Ciclo por frame (ordem importa)
1. **Update (início do frame):** heartbeat, drenar eventos, ler estado do jogo (cena, jogador, pausa), ler `McState`, decidir engatar/soltar, ler input, mouse→yaw/pitch, exportar colisão, escrever input.
2. **FixedUpdate/ticks de física:** reaplicar a pose do corpo (consistência com triggers).
3. **LateUpdate (fim do frame, ordem de execução alta):** interpolar pose do MC, mover corpo, calcular câmera, **`WriteSky` (um por frame renderizado)**.
4. **Pré-render da câmera (callback de culling):** sobrescrever posição/rotação/FOV da câmera de novo, para nada do jogo deixá-la um frame atrás.

### 5.3 Engate e spawn
- Condições para engatar: cena de jogo carregada, jogador existe, **a pé e no chão**, num corpo/terreno válido, input em modo "personagem", sem estado especial (veículo, diálogo, anexado, morto), Minecraft vivo.
- Ao engatar: descobrir o chão sob os pés (raycast, ignorando colliders do próprio jogador), medir `bodyOffset` (distância pivô→chão) e altura do olho no jogo, calcular spawn em coords MC, derivar yaw/pitch do olhar atual, `epoch++`, `teleportSeq += 2` (inicie `teleportSeq` aleatório/ímpar por execução), começar a exportar colisão, tornar o corpo **cinemático**.
- O MC só "chegou" quando `McState.teleportAck == teleportSeq && InWorld`. Até lá **não** mova a câmera nem o corpo; mantenha a câmera do jogo.

### 5.4 Mapeamento de coordenadas
- Mundo plano: afim (escala + eixos), como Skyrim (`mc.x = sky.x/70`, etc.).
- **Mundo esférico (planetas):** projeção azimutal equidistante em torno de uma âncora, `y` = altura radial, "baixo" do MC = para o centro (casa com gravidade radial). Implementação: `PlanetFrame.cs` (`McToLocal`, `LocalToMc`, `AxesAt`, `LookRotation`, `YawPitchFor`). Guarde tudo em **espaço local do corpo/planeta**, assim blocos acompanham rotação/órbita.
- **Distorção cresce com a distância da âncora** (num planeta de ~190 m, rápido). Solução implementada: **vários mapas (charts)** por planeta: âncora nova quando o jogador passa de ~60 blocos; cada chart tem sua faixa de X do MC (`slot*20000`), persistida em arquivo (`frames.txt`), nunca recalcule um chart antigo. Troca de chart em voo = **teleporte suave** com a velocidade girada para os eixos novos (seção 5.10).
- Espelhamento: MC é destro (Z sul), Unity é canhoto: use `ez = -cross(ex, ey)` e **inverta o winding** dos triângulos ao importar meshes.
- `LookRotation` deve usar `up` perpendicular à direção de olhar (`ey*cos(p) + horiz*sin(p)`), senão a visão vira ao olhar reto para cima/baixo.

### 5.5 Colisão (WorldExporter estágio A)
- Região = cubo 8×8×8 blocos. Por coluna de região, amostrar a grade a **0,5 bloco** (17×17 pontos) com **raycast multi-hit de cima para baixo** (ignorando colliders do jogador e triggers). Camadas ordenadas, duplicatas <0,04 removidas.
- Triângulos: ligar camadas vizinhas com |Δh| ≤ 1,25. **Paredes:** onde uma camada acaba contra outra mais alta, emita um quad vertical (o colisor do MC trata normal íngreme como parede; `walkable` = |ny| ≥ 0,7). Penhascos sem camada mais alta ao lado = borda (sem parede).
- Voxels: rasterizar triângulos em sub-voxels 1/8 (piso com ~½ bloco de espessura), agrupados em blocos 8×8×8 (`bits[y]` bit `z*8+x`).
- Para cada região enviar **`Tris` e depois `Region` para todas as faixas Y** de `floor(minH/8)-2` a `floor(maxH/8)+2` (inclusive vazias): o MC só libera o jogador quando regiões acima/abaixo estão "conhecidas".
- Agendamento: vizinhança de ±3 regiões, mais próximas primeiro, 2 colunas/frame (até 8 se rápido). Atualizar colunas "frescas" a cada ~2,5 s (perto) / ~12 s (longe); colunas **sem chão achado** a cada 0,25 s. Para velocidade alta, **olhe à frente** (posição + velocidade×0,5 s) e use raios longos (≈320 m).
- `epoch` diferente = MC descarta tudo. Mensagens com `epoch` antigo são ignoradas.

### 5.6 O fantoche (corpo + câmera)
- Corpo: **cinemático**, posicionar por `transform` + rigidbody **sem** chamar teleportes do jogo que disparem eventos globais (A1). Rotação = só yaw, com "cima" = normal do chão. Velocidade = velocidade do referencial (planeta/veículo) para não haver "morte por impacto" ao soltar.
- Interpolação de ticks: copiar a lógica de `skse/src/Game.cpp` (porta em `TickInterp.cs`): histórico de ticks, renderizar levemente no passado (atraso adaptativo 4-30 ms), interpolação pura, nunca extrapolação.
- Câmera: olho do MC (+ bob: lado `-sin(fase)*bob*0.5`, altura `|cos(fase)*bob|`, pitch `|cos(fase-0.2)*bob|*5°`, roll `sin(fase)*bob*3°`), FOV vertical do `McState`, F5 (`cameraMode` 1 atrás, 2 de frente espelhado; recuar por `cameraDistance` com suavização).
- **Esconda o modelo do personagem do jogo** (só o avatar Steve aparece em 3ª pessoa): desligar renderers do corpo (exceto capacete/ferramentas/câmera), restaurar ao soltar.
- Escreva `WriteSky` **todo frame**: yaw/pitch autoritativos, `teleportSeq`, flags.

### 5.7 Input
- Ler as teclas pela API que o jogo usa (no OW: `UnityEngine.InputSystem`, **nunca** `UnityEngine.Input`). Mapear para **scancodes SDL** (A-Z=4..29, 1-9=30..38, 0=39, Enter=40, Esc=41, Backspace=42, Tab=43, Space=44, F1-F12=58..69, setas 79-82, LCtrl=224, LShift=225, LAlt=226, RCtrl=228, RShift=229, RAlt=230).
- Chaves reservadas ao host: **Esc** = menu do jogo alvo (mas fecha uma tela do MC aberta primeiro), **O** = menu do MC (`InOpenMenu`), **G** = "interagir" do jogo alvo, **F8** = devolver/pegar controle, **F1** = também esconde o HUD do jogo alvo, F11 não encaminhar (fullscreen do MC escondido).
- Arbitragem de uso: `E` abre inventário do MC, **exceto** quando o jogo alvo tem um interagível na mira (então `E` = interagir do jogo). Quem está mais perto ganha, como no SkyCraft.
- Opção de gosto aplicada: Ctrl agacha, Shift corre (troque os scancodes enviados).
- Cursor: tela do MC aberta → destravar cursor e enviar posição em **pixels do overlay** (`x*overlayW/screenW`, `(screenH-y)*overlayH/screenH`).
- Texto (chat/placas): evento de texto do SO → `InText`.
- **Portão de input:** um único ponto (no OW, as funções `OWInput.GetAxisValue/GetValue/IsPressed/IsNewlyPressed/IsNewlyReleased` via Harmony prefix) devolve zero/falso para todo comando de gameplay enquanto o MC dirige, **deixando passar** pausa e Esc, e **alimentando** "interagir" com a tecla do host.

### 5.8 Render do mundo, luz e HUD
- **Blocos:** cada `Section` vira uma mesh; vértices vêm em coords relativas à seção → converter **por vértice** com o mapeamento (curva sobre o planeta); pais = corpo (ou raiz do mundo). Flags: bit0 cutout, bit1 translucent, **bit2 sólido** (extensão deste projeto), bits4-6 face. Separar submeshes por (tipo, tint quantizado) e criar **um material por combinação** (grama/folha/água precisam de tint; AO fica de fora).
- **Guarde as seções cruas.** O MC reenvia tudo **assim que o link sobe**, normalmente **antes** do jogador engatar. Se você descartar mensagens cujo "frame/planeta" ainda não existe, os blocos só aparecem quando forem editados (bug real). Mantenha `raw[key]` e reconstrua quando o frame estiver pronto e após recarregar a cena.
- **Shaders:** não existe shader customizado compilável em runtime. Liste o que a build tem (`Resources.FindObjectsOfTypeAll<Shader>()` e `Shader.Find` num probe). Escolha: sólido = shader iluminado do jogo com ZWrite (no OW: `Legacy Shaders/Diffuse`); cutout = shader com alpha-test (no OW: `Outer Wilds/Particles/Cutoff`); translúcido = blend (`Outer Wilds/Particles/Alpha`). Deixe a **iluminação do jogo** acender os blocos (sol, fogueiras): assar brilho de luz do MC em cor de vértice deu resultado azulado/chapado.
- **Luzes:** `RenLights` → poucas (≈8) luzes pontuais pooladas, as mais próximas, `ForcePixel`; tocha/lanterna na mão (`HeldLight`) = luz presa à câmera.
- **WorldEntities:** o host desenha flechas (dois quads cruzados, ícone girado 45° para a ponta seguir a direção de voo), itens soltos (sprite que gira), blocos soltos (cubo), rachaduras (cubo levemente maior, material translúcido), contorno do bloco (linhas). UV vem em coordenadas de atlas.
- **Atlas:** upload sem inverter linhas; usar UV como enviado. Texturas de entidade idem.
- **Overlay (hand+GUI):** textura RGBA premultiplicada → desenhe com material premultiplicado (blend `One, OneMinusSrcAlpha`) e **cor branca explícita**. Chamar `Graphics.DrawTexture(rect, tex)` sem cor usa 0,5 de vértice e deixa o HUD meio transparente (bug real).
- O overlay tem o aspecto da tela (`WriteSky` pede viewport = metade da tela, limitado a 1280 de largura).

### 5.9 Handoffs (o jogo alvo recupera o controle)
Soltar controle quando: veículo/nave, console, computador, diálogo, anexado/sentado, dormindo, morto, **modo de input mudou sozinho** (assar, cutscene), **dentro de fluidos/forças que só a física do jogo entende** (água, gêiseres, feixe trator, areia, nuvem, plasma), tecla F8. Regras:
- Debounce de ~0,3 s para não oscilar.
- **Handoff seguro:** antes de devolver o corpo, assente-o no chão (raycast), só então volte a dinâmico com a velocidade do referencial.
- Ao soltar, **feche qualquer tela do MC** (envie Esc) e solte todas as teclas; caso contrário ela fica presa atrás do jogo.
- Enquanto solto: `WriteSky` com `Loading` (o MC estaciona o jogador), HUD do MC só quando fizer sentido (escondido em veículos).
- Reengatar quando voltar a estar a pé, no chão, fora das forças; novo `teleportSeq`.

### 5.10 Eventos, dano, morte, voo
- Morte no MC (`kEvPlayerDied`) → matar o jogador do jogo alvo. Respawn: o lado Fabric faz `respawn()` quando o host teleporta um MC morto.
- Dano do jogo alvo → `InHurt` (`code`=tipo, `a`=dano×100): escala 100 de vida do jogo ≙ 20 corações.
- **Voo rápido (elytra, queda):** troca de chart com a flag `SoftTeleport`: o MC move o jogador **sem zerar velocidade e sem segurá-lo** esperando chão; o host manda `InVelocity` já girada para os novos eixos depois do `ack`. Teleporte normal zera a velocidade e **trava até 6 s** esperando colisão (quebra voo).
- Segurança: se `y` cair 90 blocos abaixo do último ponto seguro, teleporte para o último ponto seguro.

### 5.11 Persistência e multiplos "mundos"
- O MC guarda o mundo. O host guarda só os charts. Cada região do mundo do jogo (planeta, interior, worldspace) = sua faixa de X (ou dimensão). Troque `collisionEpoch` ao mudar de mundo.
- Recarregar cena/loop destrói objetos do jogo: reconstrua seções pendentes quando os pais reaparecem.

---

## 6. Plano em fases (cada uma com critério de pronto)

| Fase | Entrega | Pronto quando |
|---|---|---|
| 0 | Link | host cria o mapeamento, o MC abre, `McState` chega, `heartbeat` bate nos dois lados. Use `tools/fake_skyrim.py`/`wildcraft/host/fake_outerwilds.py` para testar o Minecraft sem o jogo. |
| 1 | Andar | colisão exportada, MC físico anda no terreno, corpo e câmera seguem, mouse gira, tela do MC abre (E), F5. Sem tremor com framerate diferente. |
| 2 | Overlay | HUD e mão do MC por cima, opaco e na orientação certa, inventário clicável. |
| 3 | Blocos | colocar/quebrar, blocos aparecem **ao entrar** (não só ao editar), luz do jogo os ilumina, torches viram luz, flechas/itens/cracks/contorno aparecem. |
| 4 | Convivência | handoffs (veículo, diálogo, fluidos, pausa), portão de input, arbitragem de E/G, morte nos dois sentidos. |
| 5 | Mundo grande | charts/regiões, voo rápido, persistência após recarregar. |
| 6 | Extras | combate (ActorTable), água do jogo como água do MC (`WaterGrid`), sombras, som, multiplayer, itens do jogo como itens do MC. |

---

## 7. Armadilhas conhecidas (cada uma já custou horas)

**A. Corpo/física**
- **A1.** Teleportes "oficiais" do jogo (no OW `WarpToPositionRotation`/`SetPosition`) disparam um evento global ("PlayerRepositioned") que **recentra o universo a cada chamada**: ~100 recentragens/s, jogo engasgando. Para mover um corpo cinemático por frame use `transform.SetPositionAndRotation` + posição do rigidbody. Valide contando linhas do log do jogo.
- **A2.** Soltar o corpo cinemático dentro/abaixo do terreno mata o jogador (asfixia/impacto). Faça handoff seguro (5.9) e bloqueie o hook de dano por impacto enquanto engatado.
- **A3.** O jogo pode **não informar** em que corpo/solo o jogador está (`GetLastGroundBody` veio nulo). Tenha fallback por raycast para baixo na direção da gravidade.
- **A4.** Handoff por fluido precisa ler "está dentro do volume X" (no OW `FluidDetector.InFluidType`), com tipos: geyser, trator, água, areia, nuvem, plasma.

**B. Mapeamento**
- **B1.** Um único mapa esférico distorce muito longe da âncora e em altura. Use charts (5.4).
- **B2.** Winding invertido pelo espelhamento; normais vêm do `RecalculateNormals` depois de inverter.
- **B3.** Precisão `float`: coordenadas MC ficam em centenas de milhares; sempre subtraia a origem do chart em `double` antes de converter.

**C. Protocolo**
- **C1.** `WriteSky` tem que avançar **todo frame**; o MC espera até 25 ms por esse avanço.
- **C2.** Não encher os anéis (input/colisão/render). Descarte/segure e tente depois.
- **C3.** Overlay premultiplicado + `bottomUp`.
- **C4.** Atlas sem inverter linhas, UV como enviado.
- **C5.** Heartbeat do MC some por segundos durante carregamento de cena do jogo: não trate como crash.
- **C6.** Duas instâncias do MC (resto de uma execução anterior) travam o mundo `SkyCraft` (`DirectoryLock`: "outro processo bloqueou parte do arquivo"). O launcher deve matar instâncias antigas **só deste projeto** (comando de linha contém o caminho do `fabric/`).

**D. Render**
- **D1.** Shaders só existem se estiverem na build; teste com `Shader.Find` e logue. Variantes por keyword (`_ALPHATEST_ON` do Standard) podem ter sido retiradas.
- **D2.** `Graphics.DrawTexture` sem cor = HUD translúcido.
- **D3.** Blocos "só aparecem ao editar" = mensagens descartadas antes do frame existir (5.8).
- **D4.** `LookRotation(look, worldUp)` vira de cabeça para baixo ao olhar reto para cima/baixo.
- **D5.** Janela do jogo em tela cheia exclusiva **minimiza** ao perder foco; o launcher deve abrir o MC escondido **antes** do jogo e o MC nunca pode roubar foco.

**E. Input**
- **E1.** `GetAsyncKeyState`/APIs globais leem teclas com a janela sem foco. Respeite o foco do jogo.
- **E2.** Se o MC mantiver uma tela aberta enquanto o jogo alvo tem o controle, o jogador fica preso. Feche ao soltar.
- **E3.** Atalhos do host não podem ir ao MC (O, G, F8, F11 e E quando arbitrado).

**F. Tela/teste automatizado**
- **F1.** Para testar, você precisa de: capturar tela (`System.Drawing`/`CopyFromScreen`), restaurar a janela do jogo se minimizada (`OpenIcon`), injetar teclas/mouse (`keybd_event`/`mouse_event`), ler logs. **Avise o usuário** antes de injetar input e não faça se ele estiver jogando: seu input se mistura com o dele (e você reinicia o jogo dele).
- **F2.** `sleep` longo no shell pode ser bloqueado: use um monitor que espera uma condição no log.

---

## 8. Como verificar (loop de desenvolvimento)

1. Script de ciclo: compilar o mod → matar o jogo → copiar DLL e manifest → apagar `config.json` antigo → relançar → (o MC pode continuar rodando; ele reenvia tudo quando o link sobe).
2. Logue **mudança de estado e o motivo** (`standby: <motivo>`, `engaged on <corpo>`, `released: <motivo>`), mais uma linha `trace` a cada ~4 s: posição MC, `ground`, yaw/pitch, colunas/raios exportados, seções, luzes, fps. Isso encurtou cada diagnóstico.
3. Dump de diagnóstico (arquivos na pasta do mod): lista de shaders e propriedades, resultado do probe de `Shader.Find`, `atlas_debug.png` (o que o MC realmente mandou), log das primeiras seções (uv, cor, luz, contagem de triângulos).
4. Captura de tela → olhar a imagem. Critérios visuais: HUD opaco e na orientação certa, blocos com cor/textura certas e sem z-fighting, sem corpo do jogo visível, sem distorção longe da âncora, sem tremor.
5. Contagens que denunciam bug: eventos "recentralizado" por segundo, `sections` vs seções recebidas, fps.

---

## 9. Checklist final de aceite

- [ ] Link sobe/desce sem travar nenhum dos dois; reabrir o jogo reconecta.
- [ ] Andar/pular/correr/agachar com física do MC no terreno do jogo, escadas e rampas ok, paredes bloqueiam.
- [ ] Câmera sem tremor, FOV/bob/F5 ok, olhar reto para cima/baixo ok, corpo do jogo escondido, Steve só em 3ª pessoa.
- [ ] HUD/mão opacos e corretos; inventário (E) clicável; chat/comandos funcionam; Esc fecha tela do MC antes de abrir menu do jogo.
- [ ] Blocos aparecem ao entrar no mundo (todos, inclusive atrás), iluminados pela luz do jogo, com tint; colocar/quebrar atualiza; tochas e item na mão iluminam; flechas, itens, cracks e contorno aparecem.
- [ ] Handoffs (veículo, diálogo, fluidos, F8, pausa) soltam e reengatam sem travar nem deixar tela do MC presa.
- [ ] Morte nos dois sentidos; dano do jogo reflete no MC.
- [ ] Voo rápido (elytra/queda) sem perder velocidade nem atravessar o chão; troca de chart invisível.
- [ ] Persistência: fechar e abrir o jogo mantém construções.
- [ ] Launcher de um clique que mata MC antigo, abre MC escondido, abre o jogo, documenta controles.

---

## 10. Template de prompt para colar na IA

```
Contexto: tenho o repositório SkyCraft/WildCraft (pasta anexa). Quero portar "Minecraft dentro do jogo <JOGO>".
Leia docs/PORTING_GUIDE.md inteiro, protocol/skycraft_protocol.h, docs/DESIGN.md e o host de referência em wildcraft/owml/.
Regras: não reescreva o lado Minecraft (fabric/) nem o protocolo, só crie o host para <JOGO> em wildcraft-<jogo>/ (ou pasta equivalente).
Faça por fases (seção 6), uma de cada vez, e só avance quando eu (ou você, rodando o jogo e capturando a tela/logs) confirmar o critério de pronto.
Comece pela seção 2: descubra no jogo real as respostas da tabela (engine, injeção, mover jogador, ler geometria, desenhar, input, portão de input, esfera ou plano).
Depois escreva o plano de implementação com os arquivos que vai criar e as chamadas exatas da engine de <JOGO> que vai usar, e só então implemente.
Nunca diga que funciona sem ter rodado. Registre bugs novos em docs/PORTING_GUIDE.md na seção 7.
```

---

## 11. Mapa dos arquivos de referência

| Assunto | Arquivo |
|---|---|
| Protocolo (fonte da verdade) | `protocol/skycraft_protocol.h`, `fabric/.../link/Proto.java`, `wildcraft/owml/WildCraft.Core/Proto.cs` |
| Design geral | `docs/DESIGN.md` |
| Host Skyrim (C++) | `skse/src/*.cpp` (Game.cpp = interpolação de ticks e puppet; Collision.cpp; WorldRender.cpp; Input.cpp; Link.cpp) |
| Host Outer Wilds (C#) | `wildcraft/owml/WildCraft/` (`Bridge.cs` máquina de estados, `PlanetFrame.cs` projeção e charts, `CollisionExporter.cs`, `TickInterp.cs`, `InputRouter.cs`, `GamePatches.cs` portão de input, `CameraDriver.cs`, `McCompositor.cs` overlay, `McWorldRender.cs` blocos/luzes/entidades) |
| Link em C# | `wildcraft/owml/WildCraft.Core/HostLink.cs` |
| Lado Minecraft | `fabric/src/` (`SkyClient.java` ciclo, `InputBridge.java`, `MirrorWorld.java`, `render/WorldExporter.java`, `world/SkyCollision.java`, `world/TriCollider.java`) |
| Launcher/instalação | `wildcraft/tools/JOGAR.ps1`, `install_owml_mod.ps1` |
| Testes sem o jogo | `tools/fake_skyrim.py`, `tools/fake_guest.py`, `wildcraft/host/fake_outerwilds.py` |

Extensões deste projeto ao protocolo v10 (lado Java e C# já as implementam): SkyState flag bit 3 `SoftTeleport`; McState offset 0x4C `heldLight`; Input tipo 9 `Velocity`; RenVertex flag bit 2 `solid`.
