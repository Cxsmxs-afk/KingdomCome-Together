# Working on this fork

## Standing rule

The top goal of this fork is a smooth co-op for KCD2 that feels native, and playing together should be as fun as Elden Ring Seamless Co-op or similar. Every decision is measured against that; completely new approaches are allowed when they serve it.

That is the bar for reviews, refactors and new work. A change that keeps the current machinery but still looks late, jerky or unreliable has not met it. A larger change is in scope when it is the way to meet it.

The same rule is the opening of `docs/ARCHITEKTUR-V2.md`, which is the architecture proposal for getting there. `docs/DESIGN-AUDIT.md` is the measured audit of the body path (who decides is sound; how a body is shown is not). Do not treat either document as a licence to rewrite the campaign into an MMO.

Decided for this fork: two players are the primary case and must feel the best; design and keep working for a small group of three or four in the same world, and test the pair first. The core, in order, is the players themselves, the NPCs, the open world, and the story and quests. Build what you see every second first; the story is still core, not a leftover. Chests and herbs stay per player: each person looks into their own. Dialogue is a majority vote. Shops and traders are secondary and only need to be as good as is reasonable. The Modding Tools build stays required for now. Native frame hooks are allowed and get fixed after game patches.

## Installing

Replacing DLLs and other files is fine, the way NightCoop replaces files in the game folder. Installing, updating and removing must stay simple for every player: one zip, extract it into the KCD2 Modding Tools folder, overwrite. About ten steps, written in plain German in that zip (`INSTALL.txt`). Players do not build. `.github/workflows/build-windows.yml` builds the DLL, the programs, the pak and the installer on a Windows runner and uploads them.

## What this repository is

Kingdom Come: Together is an unofficial co-op mod for Kingdom Come: Deliverance II. One player hosts their world; the other joins it. The game, CryEngine and the campaign content belong to Warhorse Studios and PLAION. This project's own code is GPL-3.0-only; see `LICENSE`, `NOTICE` and `AUTHORS`.

## How to decide

- Judge a change by how it feels with two players: remote Henry, horses, nearby people, a fight, a quest step. Bench numbers support that; they do not replace it.
- Prefer the engine's own movement, activity and combat requests over writing a pose in from outside. `docs/DESIGN-AUDIT.md` section 4.10 is the contract for a copy.
- Keep the host's save as the world. Do not start a headless simulation of KCD2; the engine does not offer one.
- Movement is newest-sample and may be dropped. Deaths, hits, quests, inventory and time are reliable and ordered. Do not put both in one FIFO.
- Per-frame work belongs in the game process. The agent is for the session, the link and the join. The debug HTTP console and `kcd.log` are not a multiplayer transport.
- A new approach has to name what it deletes. Stacking another switch on the walk, the ghost or the queue is how the current code became fragile.
- Anything that needs the real game, two Windows PCs, or the retail build without Modding Tools must be marked as unverified. Do not invent engine behaviour.

## Layout

| Piece | Where | Role |
|---|---|---|
| Lua mod | `kdcmp/Data/Scripts/Startup/kdcmp.lua` | Loaded by the game. One very large script. |
| Agent | `dotnet/KcdMp.Client` | Talks to the local game and to the relay. |
| Relay | `dotnet/KcdMp.Server` | The host's session process. TCP, and Steam when that path is up. |
| Master | `dotnet/KcdMp.MasterServer` | Optional directory of relays. Not the game world. |
| Native DLL | `native/KCDMP` | In-process hooks, the named pipe, body driving. |
| Launcher | `KCDMP_launcher` | Starts the game, the agent and the relay. |
