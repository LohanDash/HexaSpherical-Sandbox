# Planet performance audit — Generational Update

Date: 2026-07-22  
Target machine: ASUS G551JW-class laptop (Intel H-series CPU, GTX 960M)  
Scope: current 288 m **Normal** planet, subdivision level 7, 163,842 surface cells.

## Executive summary

The current renderer streams meshes and collisions, but the world data is not yet
fully streamed. Creating a planet still builds the complete icosphere, all dual-cell
rings and neighbours, every biome and every surface height before play. Increasing
subdivision by one would approximately quadruple the surface cell and triangle count,
so the present startup cost scales with the whole potential planet instead of the
visited area.

The safest high-value changes in this update are:

- Natural caves are no longer stored as millions of `HashSet<long>` entries. They are
  implicit deterministic terrain derived from seed + generator version. Compact
  two-word masks are generated only for cells in a detailed chunk.
- Player edits remain explicit deltas. Existing removed/placed voxel save fields and
  cell IDs are unchanged.
- Distant LOD still samples only the surface and never materializes underground masks.
- The FPS/TPS interaction ray now rejects non-overlapping spherical chunk caps before
  testing triangles. The rear camera no longer scans every detailed triangle each
  physics frame.
- High-altitude flight no longer builds underground voxels and concave collisions.
  It streams surface LOD only until the player returns close to the ground.
- With the mandatory zero cell gap, top and wall geometry now share the same immutable
  ring array instead of allocating a duplicate jagged `Vector3[]` for all 163,842 cells.
- Icosphere containers are pre-sized and their large temporary midpoint lookup is
  released after topology construction.
- Permanent phase, chunk, and cell counters make future regressions measurable.

This is a meaningful storage/frametime pass, not the final large-planet architecture.
The remaining dominant startup cost is global icosphere topology. A future radius or
resolution increase should use hierarchical addressable topology rather than another
global subdivision level.

## Measured current pipeline

Measurements are emitted by `PlanetGenerationTest` on the actual Godot Mono build.
Numbers vary substantially on this laptop due to editor/OS load, so phase attribution
is more useful than a single wall-clock number.

Representative optimized run:

| Metric | Result |
|---|---:|
| Startup data build | 10.97 s |
| Icosphere subdivision | 8.16 s |
| Dual-cell rings + neighbours | 0.53 s |
| Biomes + surface heights | 2.13 s |
| Cave metadata at startup | 0.010 s |
| Chunk index | 0.12 s |
| Globally addressable surface cells | 163,842 |
| Detailed cave cells after 24 streaming frames | 1,216 |
| Detailed / LOD chunks after 24 frames | 19 / 1 |
| Detailed chunk build average / maximum | 24.13 / 37.79 ms |
| Maximum observed streaming frame | 46.80 ms |
| Managed memory after streamed sample | 41.4 MiB |

A deliberately extreme 120-frame test teleported the streaming target rapidly around
the globe at high altitude. Before altitude-aware streaming it averaged 36.87 ms/frame,
reached a 144.24 ms frame, and reported 33 FPS. With surface-only LOD at altitude it
averaged 6.89 ms/frame and reported 105 FPS. The 44.05 ms overall maximum in the
optimized run came from the initial near-ground detailed chunk, not the flight
traversal. These are headless CPU/engine measurements, not GTX 960M rendered benchmarks.

The pre-change wall-clock sample was 8.97 s, but it did not expose phase or live chunk
metrics and was recorded under a different machine load. It must not be presented as a
scientific before/after speedup. The reliable improvements are structural: cave startup
work is now about 10 ms, cave voxel storage is bounded/compact, a duplicate per-cell
geometry array is removed, and TPS rays inspect a local cap rather than the planet.

The test reaches its first collision chunk one streamed frame after the synchronous
data build. Therefore “time until movement” is still dominated by global topology and
biome construction. Headless tests cannot report a meaningful rendered FPS; they report
CPU frame and chunk times instead. A release-build, on-device profiling capture remains
required for GPU FPS, VRAM and a fast-flight traversal.

## Current architecture and scaling

### Work performed globally at startup

1. **Topology:** generate 163,842 normalized icosphere vertices and 327,680 triangular
   faces. A midpoint dictionary preserves stable cell IDs.
2. **Face adjacency:** attach five or six triangle centers to each surface vertex.
3. **Terrain:** evaluate the versioned biome generator and one surface height per cell.
4. **Cave entrances:** evaluate only rare deterministic entrance metadata globally.
5. **Dual cells:** build each pentagonal/hexagonal ring and its neighbour IDs.
6. **Chunk index:** geographically sort cells into 64-cell chunks and calculate chunk
   bounding caps.
7. **Streaming:** only then queue nearby LOD meshes and detailed meshes/collisions.

The system does **not** allocate one C# object per voxel. Surface arrays are contiguous,
but ring and neighbour topology remains jagged. Natural underground voxels are now
implicit; only loaded detailed cells receive compact cave masks. Save data already
stores player removal/placement deltas rather than a full world dump.

### Frequent paths

- Direction-to-cell uses neighbour hill-climbing and does not linearly scan all cells.
- Streaming currently checks all chunk caps every 12 frames. At the current ~2,560
  chunks this is acceptable, but a hierarchy will be needed at larger subdivision.
- Chunk mesh construction is one whole chunk on the main thread per rendered frame.
  This prevents batches of chunks in one frame, but a single 20–40 ms chunk can still
  stutter.
- Godot `SurfaceTool`, mesh commit, collision creation, and scene-tree mutation remain
  on the main thread.

## PlanetSmith research: known facts versus inference

PlanetSmith publicly confirms spherical procedural hex-voxel worlds, planets around
100 km², and planet-scale rendering from space to ground. Its public website and press
kit do **not** disclose its internal chunk format, voxel compression, worker model, or
LOD algorithm. Claims that it specifically uses an octree, greedy meshing, a particular
database, or a specific compression scheme would therefore be speculation.

Public comparable implementations do support the long-term direction. Cuberact's open
Godot planet demo uses a cube-sphere, quadtree chunk LOD, fixed-size per-chunk grids,
chunk pooling, shader displacement, frustum culling, and angular horizon culling. The
older Planet Nomads engineering write-up describes a native procedural terrain engine
for large destructible spherical planets and explicitly identifies multithreading,
memory management, LOD, and physics as core problems.

Godot's own documentation says procedural geometry is CPU-generated, `ArrayMesh` is
slightly faster than `SurfaceTool`, and the active scene tree is not thread-safe.
Consequently, future workers should produce plain immutable mesh/data buffers; main
thread code should commit Godot resources and collision nodes under a strict budget.

Sources:

- PlanetSmith official site: https://www.planetsmith.world/
- PlanetSmith official press kit: https://www.planetsmith.world/press
- Cuberact open Godot chunked-LOD planet: https://www.cuberact.org/projects/planet-chunked-lod/
- Planet Nomads spherical voxel engineering article: https://www.gamedeveloper.com/programming/bending-unity-to-carry-spherical-voxel-planets-in-planet-nomads
- Godot thread-safe API guidance: https://docs.godotengine.org/en/4.0/tutorials/performance/thread_safe_apis.html
- Godot procedural geometry guidance: https://docs.godotengine.org/en/4.0/tutorials/3d/procedural_geometry/index.html
- Godot performance monitors: https://docs.godotengine.org/en/4.3/classes/class_performance.html

## Safe long-term design

The next architecture should retain only stable addressing data globally:

```text
World seed + generator version + sparse edit deltas
                         |
Hierarchical spherical chunk address (face/path/LOD)
                         |
       +-----------------+-----------------+
       |                                   |
Surface LOD sample                    Detailed playable chunk
height + biome only                   crust + caves + edits
       |                                   |
cheap visual mesh                 mesh + local collision
```

Recommended order:

1. Replace the flat chunk list with a hierarchical icosphere or cube-sphere address.
2. Preserve legacy cell IDs through an explicit V2/V3 addressing adapter; never reinterpret
   existing edit keys.
3. Generate far LOD directly from seed/height samples without detailed cell rings.
4. Materialize only a bounded playable crust near the player. The current theoretical
   layer span is small enough that the centre is already absent; keep this property.
5. Move pure biome, noise, occupancy and mesh-buffer preparation to one worker queue.
6. Commit `ArrayMesh`, collision resources, and nodes on the main thread with a measured
   millisecond budget and separate priorities for collision, near visual, far LOD, and
   prefetch.
7. Add chunk data eviction and a small LRU cache. Reconstruct untouched chunks from seed;
   reapply sparse edits on load.
8. Add horizon culling so the far side of the planet is never queued or rendered.

## Compatibility and invariants

- `GenerationPreset == "Indev"` intentionally remains the serialized identifier. Only
  the player-facing name changed to **Normal**. Renaming the stored value would break
  old saves.
- PreIndev keeps radius, subdivisions, legacy height fingerprints, and generation version 0.
- V2 and V3 keep their recorded terrain generation versions and cell IDs.
- Natural caves remain deterministic and are never serialized as player edits.
- Placed blocks override an implicit cave cell exactly as they previously removed that
  cell from the materialized cave set.
- Save recovery, monotonic generations, checksums and sparse voxel deltas are unchanged.

The definition of success for the next large-planet phase is: increasing potential
surface area increases the address space and exploration capacity, but startup work
remains bounded by the immediately visible hierarchy and not by every possible cell.
