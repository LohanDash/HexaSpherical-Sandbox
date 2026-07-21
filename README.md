# HexaSpherical Sandbox — Alpha 0.0.3

An early Godot 4 C# prototype featuring a procedurally generated spherical planet
tiled with hexagonal cells and 12 structural pentagons.

The Alpha 0.0.3 planet has a radius of 36 metres and approximately 10,000 cells,
generated progressively, one chunk at a time.

The terrain uses spherical streaming around the player. The nearest 12 metres
display complete voxels and caves, the area up to 26 metres uses a simplified
surface, and more distant chunks are unloaded.

The underground contains interconnected chambers and tunnels. Rare natural shafts
can connect the surface directly to the cave network.

## Running the game

Double-click `Open HexaSpherical Sandbox.bat`, then press `F6`/`F5`. The launcher
explicitly configures the .NET SDK to avoid `PATH` issues after a new installation.
You can also open `project.godot` with **Godot Engine (Mono)** after restarting your
Windows session.

- ZQSD or WASD: move
- Mouse: FPS camera
- Space: jump
- Escape: release or capture the mouse
- Left click: break the targeted block
- Right click: place a block on the targeted cell
- F5: switch between first-person and third-person view
- G: toggle the directional flashlight
- Double-tap Space: toggle creative flight
- Space / Shift while flying: ascend / descend
- F6: toggle smooth interpolation
- F8: toggle spherical local weather
- F10: save and return to the main menu

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
