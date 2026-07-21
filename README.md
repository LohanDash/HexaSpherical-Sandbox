# HexaSpherical Sandbox — Alpha Indev

An early Godot 4 C# prototype featuring a procedurally generated spherical planet
tiled with hexagonal cells and 12 structural pentagons.

New Alpha Indev worlds use a planet with a radius of 288 metres eight times the
PreIndev radius and approximately 164,000 cells. The original 36 metre planet
remains available through the **PreIndev** generation preset. Existing worlds
automatically keep their original generation and are never resized.

The terrain uses spherical streaming around the player. The nearest 12 metres
display complete voxels and caves, the area up to 26 metres uses a simplified
surface, and more distant chunks are unloaded.

The underground contains interconnected chambers and tunnels. Rare natural shafts
can connect the surface directly to the cave network. Alpha Indev caves preserve
three solid layers beneath normal terrain and use much rarer surface entrances, so
the cave field cannot flatten or perforate entire landscapes.

## Running the game

Double-click `Open HexaSpherical Sandbox.bat`, then press `F6`/`F5`. The launcher
explicitly configures the .NET SDK to avoid `PATH` issues after a new installation.
You can also open `project.godot` with **Godot Engine (Mono)** after restarting your
Windows session.

- ZQSD or WASD: move
- Mouse: FPS camera
- Space: jump
- Escape: open or close the quick settings menu to quit the game, change the render distance and more in futur updates
- Left click: break the targeted block
- Right click: place a block on the targeted cell
- F5: cycle FPS → rear TPS → selfie view
- E: open the inventory and built-in 3 × 3 crafting grid
- Hold left click in Survival: progressively mine a block

## Early Survival loop

- Pick up fallen twigs beneath occasional trees.
- Place three twigs vertically to craft a stick.
- Dig stone-bearing ground with a stick to obtain a pebble without breaking the block.
- Craft an axe with one pebble above two vertical sticks.
- Craft a Primitive Pickaxe with three pebbles above two vertical sticks. It mines
  exactly four stone blocks, then returns one stick and one pebble.
- Use stone blocks to craft the much more durable Stone Pickaxe and Stone Axe.
- Only an axe can fell a tree and produce wood.
- Eight wood around an empty crafting centre produce a campfire.
- Use raw meat near a campfire to cook it. Eating it raw causes lethal food poisoning.
- G: toggle the directional flashlight
- Double-tap Space: toggle creative flight
- Space / Shift while flying: ascend / descend
- Shift while grounded: sprint
- T or /: open chat and commands
- 1–9 or mouse wheel: select a hotbar slot
- E: open the blocks and spawn-eggs inventory
- F6: toggle smooth interpolation
- F8: toggle spherical local weather
- Escape: pause the game and adjust render distance
- F10: save and return to the main menu

Survival mode includes health hearts, fall and monster damage, death, respawning,
two nocturnal enemy types, and a persistent hex-block stack in the hotbar. Creative
flight moves faster than walking. Use `/gamemode creative`, `/gamemode survival`,
`/gamemode 1`, or `/gamemode 0` to change modes while playing.

The nine-slot hotbar and 27-slot inventory start empty in new worlds. Stacks can
be dragged between the hotbar, inventory, and persistent 3 × 3 crafting grid;
right-click splits a stack. Creative mode provides a separate item catalogue.
Placed block types, inventory slots, crafting slots, and tool durability persist
in world saves.

Block metadata is centralized with stable IDs, colours, names, and reserved texture
paths under `Textures/Blocks/`, preparing both terrain rendering and inventory icons
for future texture assets without changing the save format.

A full day/night cycle lasts five minutes by default. Local time also depends on
the player's position on the planet, so travelling around the globe naturally
moves the sun and moon across the sky.

## Worlds and saves

The main menu can create, load, and delete worlds with Creative or Survival mode,
quality settings, weather, and interpolation preferences. World seeds are immutable.
Saves use versioned snapshots, checksums, a single background writer, and automatic
recovery from `world.save`, `world.tmp`, or `world.backup`.

## Living ecosystem

Cows and chickens inhabit loaded terrain chunks. Starling murmurations react to an
aerial predator, weather moves around the spherical planet, and nocturnal wildlife
appears after dark. Entity populations and important ecosystem state are saved with
the world, while distant chunks remain unloaded for performance.

## Dynamic music

The surface alternates between Aria Math and Mice on Venus using a probabilistic
rotation. Entering a cave stops surface music. After a delay of 5 to 10 seconds,
the game makes a 1% roll every second to play 13. Once triggered, it cannot play
again until the player exits and re-enters a cave. Dire Dire Docks, Far, Chirp,
Mellohi, and Stal are registered for future ocean, space, Survival death, Hardcore
death, and jukebox contexts.

MP3 files placed in `Music/` are optional local resources and are not distributed
in the public repository. The game remains functional when they are missing.

Structural pentagons are coloured purple to keep them visible in this prototype.
Terrain generation is deterministic and uses each world's immutable seed.
