using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HexaSphericalSandbox;

public partial class HexPlanet : StaticBody3D
{
    [Signal] public delegate void HighDetailChunkGeneratedEventHandler(Vector3 direction);
    [Signal] public delegate void VoxelEditedEventHandler(Vector3 direction);
    [Export] public float Radius { get; set; } = 36.0f;
    [Export(PropertyHint.Range, "1,8,1")] public int Subdivisions { get; set; } = 5;
    [Export] public int Seed { get; set; } = 73421;
    [Export] public float Relief { get; set; } = 2.8f;
    [Export(PropertyHint.Range, "0.0,0.18,0.01")] public float CellGap { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0.5,4.0,0.1")] public float BlockHeight { get; set; } = 1.3f;
    [Export] public float HighDetailDistance { get; set; } = 12.0f;
    [Export] public float StreamingDistance { get; set; } = 26.0f;

    private readonly List<Vector3> _vertices = [];
    private readonly List<(int A, int B, int C)> _faces = [];
    private readonly Dictionary<long, int> _midpoints = [];
    private Vector3[][] _cellRings = [];
    private Vector3[][] _cellDirections = [];
    private int[][] _cellNeighbors = [];
    private int[] _cellLevels = [];
    private readonly HashSet<long> _removedVoxels = [];
    private readonly HashSet<long> _playerRemovedVoxels = [];
    private readonly HashSet<long> _playerPlacedVoxels = [];
    private readonly Dictionary<long, int> _placedVoxelTypes = [];
    private const int MinimumLayer = -4;
    private const int MaximumLayer = 24;
    private const int CellsPerChunk = 64;
    private readonly Dictionary<int, MeshInstance3D> _chunkMeshes = [];
    private readonly Dictionary<int, CollisionShape3D> _chunkCollisions = [];
    private StandardMaterial3D _terrainMaterial = null!;
    private const int BlockAtlasTileSize = 16;
    private readonly Queue<(int Chunk, int Lod)> _pendingChunks = [];
    private readonly HashSet<int> _generatedChunks = [];
    private readonly HashSet<int> _everGeneratedHighDetail = [];
    private readonly Dictionary<int, int> _chunkLods = [];
    private readonly HashSet<int> _dirtyChunks = [];
    private int[][] _chunkCells = [];
    private int[] _cellToChunk = [];
    private Vector3[] _chunkDirections = [];
    private float[] _chunkAngularRadii = [];
    private Node3D _streamingTarget = null!;
    private int _streamingFrame;
    private int _closestCellHint;

    public float GenerationProgress { get; private set; }
    public int PhysicsChunkCount => _chunkCollisions.Count;
    public int LastBrokenBlockType { get; private set; }
    private float CellSelectionRadius => Radius * 0.92f / (1 << Subdivisions);
    private float StoneHeightThreshold => Radius > 100f ? 18f : 1.25f;

    public override void _Ready()
    {
        if (GameSession.Current != null)
        {
            Seed = GameSession.Current.Seed;
            bool indevPlanet = GameSession.Current.GenerationPreset == "Indev";
            Radius = indevPlanet ? 288f : 36f;
            Subdivisions = indevPlanet ? 7 : 5;
            Relief = indevPlanet ? 22f : 2.8f;
            // Dual-cell borders are shared exactly. Any positive inset creates
            // visible cracks, so both presets deliberately remain gapless.
            CellGap = 0f;
            switch (GameSession.Current.Quality)
            {
                case "High": HighDetailDistance = 18f; StreamingDistance = 38f; break;
                case "Balanced": HighDetailDistance = 12f; StreamingDistance = 26f; break;
                default: HighDetailDistance = 9f; StreamingDistance = 20f; break;
            }
            if (GameSession.Current.RenderDistance > 0f)
                ApplyRenderDistance(GameSession.Current.RenderDistance);
        }
        GenerateIcosphere();
        BuildHexPlanet();
        _streamingTarget = GetNode<Node3D>("../Player");
        RefreshStreaming(Vector3.Up);
    }

    public void SetRenderDistance(float distance)
    {
        ApplyRenderDistance(distance);
        if (IsInstanceValid(_streamingTarget))
            RefreshStreaming(_streamingTarget.GlobalPosition.Normalized());
    }

    private void ApplyRenderDistance(float distance)
    {
        StreamingDistance = Mathf.Clamp(distance, 12f, 96f);
        HighDetailDistance = Mathf.Clamp(StreamingDistance * 0.45f, 8f, 32f);
    }

    public override void _Process(double delta)
    {
        if (++_streamingFrame % 12 == 0)
            RefreshStreaming(_streamingTarget.GlobalPosition.Normalized());

        while (_pendingChunks.Count > 0)
        {
            var request = _pendingChunks.Dequeue();
            bool dirty = _dirtyChunks.Contains(request.Chunk);
            if (!dirty && _chunkLods.TryGetValue(request.Chunk, out int currentLod) && currentLod == request.Lod)
                continue;
            RebuildChunk(request.Chunk, request.Lod);
            int chunk = request.Chunk;
            _dirtyChunks.Remove(chunk);
            _generatedChunks.Add(chunk);
            _chunkLods[chunk] = request.Lod;
            if (request.Lod == 0 && _everGeneratedHighDetail.Add(chunk))
                EmitSignal(SignalName.HighDetailChunkGenerated, _chunkDirections[chunk]);
            GenerationProgress = _generatedChunks.Count / (float)_chunkCells.Length;
            break; // One terrain chunk per rendered frame.
        }
    }

    public float SurfaceRadius(Vector3 direction)
    {
        int cell = ClosestCell(direction.Normalized());
        for (int layer = _cellLevels[cell]; layer >= MinimumLayer; layer--)
            if (IsOccupied(cell, layer)) return Radius + layer * BlockHeight;
        return Radius + (MinimumLayer - 1) * BlockHeight;
    }

    public bool IsPassiveMobHabitat(Vector3 direction)
    {
        direction = direction.Normalized();
        int cell = ClosestCell(direction);
        int layer = _cellLevels[cell];
        if (!IsOccupied(cell, layer)) return false;
        float height = layer * BlockHeight;
        float latitude = Mathf.Abs(direction.Y);
        bool stone = height > StoneHeightThreshold;
        bool grass = height >= -0.45f && latitude <= 0.78f
            && !(latitude < 0.25f && height < 0.35f);
        return stone || grass;
    }

    public bool IsOcean(Vector3 direction)
    {
        int cell = ClosestCell(direction.Normalized());
        return _cellLevels[cell] * BlockHeight < -0.45f;
    }

    public Vector3 PassiveMobSurfacePosition(Vector3 direction, float clearance = 0.08f)
    {
        direction = direction.Normalized();
        return direction * (SurfaceRadius(direction) + clearance);
    }

    public int ChunkAt(Vector3 direction)
    {
        int cell = ClosestCell(direction.Normalized());
        return _cellToChunk.Length > cell ? _cellToChunk[cell] : -1;
    }

    public bool IsChunkStreamed(int chunk)
    {
        return chunk >= 0 && chunk < _chunkCells.Length
            && _generatedChunks.Contains(chunk)
            && _chunkMeshes.TryGetValue(chunk, out MeshInstance3D? mesh)
            && mesh.Mesh != null;
    }

    public bool HasPhysicsNear(Vector3 direction)
    {
        int chunk = ChunkAt(direction);
        return chunk >= 0 && _chunkCollisions.TryGetValue(chunk, out CollisionShape3D? collision)
            && !collision.Disabled && collision.Shape != null;
    }

    public bool ResolvePassiveMobSurface(Vector3 direction, ref int cachedCell,
        out Vector3 position, out int chunk)
    {
        direction = direction.Normalized();
        cachedCell = ClosestCellNear(direction, cachedCell);
        chunk = cachedCell >= 0 ? _cellToChunk[cachedCell] : -1;
        if (cachedCell < 0 || !IsPassiveMobHabitatCell(cachedCell))
        {
            position = Vector3.Zero;
            return false;
        }
        position = direction * (Radius + _cellLevels[cachedCell] * BlockHeight + 0.08f);
        return true;
    }

    private bool IsPassiveMobHabitatCell(int cell)
    {
        int layer = _cellLevels[cell];
        if (!IsOccupied(cell, layer)) return false;
        float height = layer * BlockHeight;
        float latitude = Mathf.Abs(_vertices[cell].Y);
        bool stone = height > StoneHeightThreshold;
        bool grass = height >= -0.45f && latitude <= 0.78f
            && !(latitude < 0.25f && height < 0.35f);
        return stone || grass;
    }

    private int ClosestCellNear(Vector3 direction, int startCell)
    {
        if (startCell < 0 || startCell >= _vertices.Count) return ClosestCell(direction);
        int current = startCell;
        float currentDot = direction.Dot(_vertices[current]);
        // A walking mob crosses adjacent cells, so a short local hill-climb is enough.
        for (int step = 0; step < 8; step++)
        {
            int best = current;
            float bestDot = currentDot;
            foreach (int neighbour in _cellNeighbors[current])
            {
                if (neighbour < 0) continue;
                float dot = direction.Dot(_vertices[neighbour]);
                if (dot > bestDot) { best = neighbour; bestDot = dot; }
            }
            if (best == current) break;
            current = best;
            currentDot = bestDot;
        }
        return current;
    }

    public float FloorRadius(Vector3 direction, float maximumTopRadius)
    {
        int cell = ClosestCell(direction.Normalized());
        for (int layer = _cellLevels[cell]; layer >= MinimumLayer; layer--)
        {
            float top = Radius + layer * BlockHeight;
            if (top <= maximumTopRadius && IsOccupied(cell, layer)) return top;
        }
        return Radius + (MinimumLayer - 1) * BlockHeight;
    }

    public bool HasRoom(Vector3 direction, float feetRadius, float bodyHeight)
    {
        int cell = ClosestCell(direction.Normalized());
        float headRadius = feetRadius + bodyHeight;
        for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
        {
            if (!IsOccupied(cell, layer)) continue;
            float blockTop = Radius + layer * BlockHeight;
            float blockBottom = blockTop - BlockHeight;
            if (blockBottom < headRadius - 0.04f && blockTop > feetRadius + 0.08f)
                return false;
        }
        return true;
    }

    public bool HasCeiling(Vector3 worldPosition, float headOffset = 0.7f)
    {
        int cell = ClosestCell(worldPosition.Normalized());
        float headRadius = worldPosition.Length() + headOffset;
        for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
        {
            if (!IsOccupied(cell, layer)) continue;
            float blockBottom = Radius + (layer - 1) * BlockHeight;
            if (blockBottom > headRadius + 0.15f) return true;
        }
        return false;
    }

    public bool TryEdit(Vector3 rayOrigin, Vector3 rayDirection, int levelDelta, int blockType = 0)
    {
        int bestCell = -1;
        int bestLayer = 0;
        int bestAxialFace = 0;
        int bestSideEdge = -1;
        float bestDistance = float.MaxValue;
        float selectionRadius = CellSelectionRadius;
        rayDirection = rayDirection.Normalized();

        foreach (int chunk in _generatedChunks)
        {
            if (_chunkLods.GetValueOrDefault(chunk, 1) != 0) continue;
            foreach (int cell in _chunkCells[chunk])
            {
                Vector3 normal = _vertices[cell];
                for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
                {
                    if (!IsOccupied(cell, layer)) continue;
                    if (RayIntersectsVoxel(rayOrigin, rayDirection, cell, layer, selectionRadius,
                        out float distance, out int axialFace, out int sideEdge)
                        && distance <= 9f && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestCell = cell;
                        bestLayer = layer;
                        bestAxialFace = axialFace;
                        bestSideEdge = sideEdge;
                    }
                }
            }
        }

        if (bestCell < 0) return false;
        int editedCell = bestCell;
        int editedLayer = bestLayer;
        if (levelDelta < 0)
        {
            long key = VoxelKey(bestCell, bestLayer);
            LastBrokenBlockType = _placedVoxelTypes.GetValueOrDefault(key, NaturalBlockType(_vertices[bestCell], bestLayer * BlockHeight));
            _removedVoxels.Add(key);
            _playerRemovedVoxels.Add(key);
            _playerPlacedVoxels.Remove(key);
            _placedVoxelTypes.Remove(key);
        }
        else
        {
            int placementCell = bestCell;
            int placementLayer = bestLayer;
            if (bestAxialFace > 0) placementLayer++;
            else if (bestAxialFace < 0) placementLayer--;
            else if (bestSideEdge >= 0) placementCell = _cellNeighbors[bestCell][bestSideEdge];
            if (placementCell < 0 || placementLayer < MinimumLayer || placementLayer > MaximumLayer
                || IsOccupied(placementCell, placementLayer) || WouldOverlapPlayer(placementCell, placementLayer)) return false;
            editedCell = placementCell;
            editedLayer = placementLayer;
            long key = VoxelKey(placementCell, placementLayer);
            _removedVoxels.Remove(key);
            _playerRemovedVoxels.Remove(key);
            _playerPlacedVoxels.Add(key);
            _placedVoxelTypes[key] = Mathf.Clamp(blockType, 0, 5);
            _cellLevels[placementCell] = Math.Max(_cellLevels[placementCell], placementLayer);
        }
        int editedChunk = _cellToChunk[editedCell];
        RebuildChunk(editedChunk, 0);
        _generatedChunks.Add(editedChunk);
        _chunkLods[editedChunk] = 0;
        _dirtyChunks.Remove(editedChunk);

        // Removing or adding a voxel also changes which side faces are exposed
        // in neighbouring cells. Rebuild those chunks progressively.
        foreach (int neighbour in _cellNeighbors[editedCell])
        {
            if (neighbour < 0) continue;
            int neighbourChunk = _cellToChunk[neighbour];
            if (neighbourChunk == editedChunk || !_generatedChunks.Contains(neighbourChunk)) continue;
            _dirtyChunks.Add(neighbourChunk);
            int lod = _chunkLods.GetValueOrDefault(neighbourChunk, 0);
            _pendingChunks.Enqueue((neighbourChunk, lod));
        }
        EmitSignal(SignalName.VoxelEdited, _vertices[editedCell]);
        return true;
    }

    public float GetRayHitDistance(Vector3 rayOrigin, Vector3 rayDirection, float maximumDistance = 18.0f)
    {
        float bestDistance = maximumDistance;
        float selectionRadius = CellSelectionRadius;
        rayDirection = rayDirection.Normalized();
        foreach (int chunk in _generatedChunks)
        {
            if (_chunkLods.GetValueOrDefault(chunk, 1) != 0) continue;
            foreach (int cell in _chunkCells[chunk])
            {
                Vector3 normal = _vertices[cell];
                for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
                {
                    if (!IsOccupied(cell, layer)) continue;
                    if (RayIntersectsVoxel(rayOrigin, rayDirection, cell, layer, selectionRadius,
                        out float distance, out _, out _) && distance < bestDistance)
                        bestDistance = distance;
                }
            }
        }
        return bestDistance;
    }

    public bool TryGetTargetBlockType(Vector3 rayOrigin, Vector3 rayDirection, out int blockType, float maximumDistance = 9f)
    {
        float nearest = maximumDistance;
        int nearestCell = -1;
        int nearestLayer = 0;
        float selectionRadius = CellSelectionRadius;
        rayDirection = rayDirection.Normalized();
        foreach (int chunk in _generatedChunks)
        {
            if (_chunkLods.GetValueOrDefault(chunk, 1) != 0) continue;
            foreach (int cell in _chunkCells[chunk])
            for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
            {
                if (!IsOccupied(cell, layer)) continue;
                if (!RayIntersectsVoxel(rayOrigin, rayDirection, cell, layer, selectionRadius,
                    out float distance, out _, out _) || distance >= nearest) continue;
                nearest = distance;
                nearestCell = cell;
                nearestLayer = layer;
            }
        }
        if (nearestCell < 0) { blockType = -1; return false; }
        long key = VoxelKey(nearestCell, nearestLayer);
        blockType = _placedVoxelTypes.GetValueOrDefault(key,
            NaturalBlockType(_vertices[nearestCell], nearestLayer * BlockHeight));
        return true;
    }

    private bool RayIntersectsVoxel(Vector3 origin, Vector3 direction, int cell, int layer,
        float radius, out float distance, out int axialFace, out int sideEdge)
    {
        Vector3 normal = _vertices[cell];
        Vector3 center = normal * (Radius + (layer - 0.5f) * BlockHeight);
        Vector3 relative = origin - center;
        float axialOrigin = relative.Dot(normal);
        float axialDirection = direction.Dot(normal);
        float halfHeight = BlockHeight * 0.5f;
        float axialEnter = float.NegativeInfinity, axialExit = float.PositiveInfinity;
        if (Mathf.Abs(axialDirection) < 0.00001f)
        {
            if (Mathf.Abs(axialOrigin) > halfHeight) { distance = 0; axialFace = 0; sideEdge = -1; return false; }
        }
        else
        {
            float a = (-halfHeight - axialOrigin) / axialDirection;
            float b = ( halfHeight - axialOrigin) / axialDirection;
            axialEnter = Math.Min(a, b); axialExit = Math.Max(a, b);
        }

        Vector3 tangentOrigin = relative - normal * axialOrigin;
        Vector3 tangentDirection = direction - normal * axialDirection;
        float quadraticA = tangentDirection.LengthSquared();
        float cylinderEnter = float.NegativeInfinity, cylinderExit = float.PositiveInfinity;
        if (quadraticA < 0.000001f)
        {
            if (tangentOrigin.LengthSquared() > radius * radius) { distance = 0; axialFace = 0; sideEdge = -1; return false; }
        }
        else
        {
            float quadraticB = 2f * tangentOrigin.Dot(tangentDirection);
            float quadraticC = tangentOrigin.LengthSquared() - radius * radius;
            float discriminant = quadraticB * quadraticB - 4f * quadraticA * quadraticC;
            if (discriminant < 0f) { distance = 0; axialFace = 0; sideEdge = -1; return false; }
            float root = Mathf.Sqrt(discriminant);
            cylinderEnter = (-quadraticB - root) / (2f * quadraticA);
            cylinderExit = (-quadraticB + root) / (2f * quadraticA);
        }
        float enter = Math.Max(axialEnter, cylinderEnter);
        float exit = Math.Min(axialExit, cylinderExit);
        if (exit < Math.Max(enter, 0.05f)) { distance = 0; axialFace = 0; sideEdge = -1; return false; }
        bool startedInside = enter < 0.05f;
        distance = startedInside ? exit : enter;
        // When the camera is close enough to start inside the selection
        // volume, use the boundary being exited instead of the entry face.
        bool axial = startedInside
            ? axialExit <= cylinderExit + 0.0001f
            : axialEnter >= cylinderEnter - 0.0001f;
        Vector3 hit = origin + direction * distance;
        axialFace = axial ? (hit.Dot(normal) > center.Dot(normal) ? 1 : -1) : 0;
        sideEdge = axial ? -1 : ClosestEdge(cell, hit - normal * hit.Dot(normal));
        return true;
    }

    private int ClosestEdge(int cell, Vector3 tangentHit)
    {
        Vector3 normal = _vertices[cell];
        Vector3 hitDirection = tangentHit.Normalized();
        int best = 0; float bestDot = float.NegativeInfinity;
        Vector3[] ring = _cellDirections[cell];
        for (int edge = 0; edge < ring.Length; edge++)
        {
            Vector3 midpoint = (ring[edge] + ring[(edge + 1) % ring.Length]).Normalized();
            Vector3 tangent = (midpoint - normal * midpoint.Dot(normal)).Normalized();
            float dot = tangent.Dot(hitDirection);
            if (dot > bestDot) { bestDot = dot; best = edge; }
        }
        return best;
    }

    private bool WouldOverlapPlayer(int cell, int layer)
    {
        if (!IsInstanceValid(_streamingTarget)) return false;
        Vector3 direction = _vertices[cell];
        float blockBottom = Radius + (layer - 1) * BlockHeight;
        float blockTop = blockBottom + BlockHeight;
        float playerFeet = _streamingTarget.GlobalPosition.Length() - 0.78f;
        float playerHead = playerFeet + 1.5f;
        if (blockTop <= playerFeet + 0.03f || blockBottom >= playerHead - 0.03f) return false;
        // Reject only the cell containing the player's centre. Using the full
        // (large) hex radius here made every nearby side placement impossible.
        return ClosestCell(_streamingTarget.GlobalPosition.Normalized()) == cell;
    }

    private void GenerateIcosphere()
    {
        _vertices.Clear();
        _faces.Clear();
        float t = (1.0f + Mathf.Sqrt(5.0f)) * 0.5f;
        Vector3[] initial =
        [
            new(-1,t,0), new(1,t,0), new(-1,-t,0), new(1,-t,0),
            new(0,-1,t), new(0,1,t), new(0,-1,-t), new(0,1,-t),
            new(t,0,-1), new(t,0,1), new(-t,0,-1), new(-t,0,1)
        ];
        foreach (var vertex in initial) _vertices.Add(vertex.Normalized());

        int[] indices =
        [
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
        ];
        for (int i = 0; i < indices.Length; i += 3)
            _faces.Add((indices[i], indices[i + 1], indices[i + 2]));

        for (int level = 0; level < Subdivisions; level++)
        {
            _midpoints.Clear();
            var next = new List<(int, int, int)>(_faces.Count * 4);
            foreach (var (a, b, c) in _faces)
            {
                int ab = Midpoint(a, b);
                int bc = Midpoint(b, c);
                int ca = Midpoint(c, a);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }
            _faces.Clear();
            _faces.AddRange(next);
        }
    }

    private int Midpoint(int a, int b)
    {
        int low = Math.Min(a, b);
        int high = Math.Max(a, b);
        long key = ((long)low << 32) | (uint)high;
        if (_midpoints.TryGetValue(key, out int cached)) return cached;
        int index = _vertices.Count;
        _vertices.Add((_vertices[a] + _vertices[b]).Normalized());
        _midpoints[key] = index;
        return index;
    }

    private void BuildHexPlanet()
    {
        var adjacentFaces = Enumerable.Range(0, _vertices.Count)
            .Select(_ => new List<int>()).ToArray();
        var centers = new Vector3[_faces.Count];

        for (int faceIndex = 0; faceIndex < _faces.Count; faceIndex++)
        {
            var face = _faces[faceIndex];
            centers[faceIndex] = (_vertices[face.A] + _vertices[face.B] + _vertices[face.C]).Normalized();
            adjacentFaces[face.A].Add(faceIndex);
            adjacentFaces[face.B].Add(faceIndex);
            adjacentFaces[face.C].Add(faceIndex);
        }

        _cellRings = new Vector3[_vertices.Count][];
        _cellDirections = new Vector3[_vertices.Count][];
        _cellNeighbors = new int[_vertices.Count][];
        if (_cellLevels.Length != _vertices.Count)
        {
            _cellLevels = new int[_vertices.Count];
            for (int cell = 0; cell < _vertices.Count; cell++)
                _cellLevels[cell] = Mathf.Clamp(Mathf.RoundToInt(GeneratedHeightAt(_vertices[cell]) / BlockHeight), MinimumLayer, MaximumLayer);
            GenerateCaves();
            ApplySavedVoxelChanges();
        }

        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            Vector3 normal = _vertices[cell];
            Vector3 axisX = normal.Cross(Mathf.Abs(normal.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 axisY = normal.Cross(axisX).Normalized();
            int[] orderedFaces = adjacentFaces[cell]
                .OrderBy(index => Mathf.Atan2(centers[index].Dot(axisY), centers[index].Dot(axisX)))
                .ToArray();
            var ring = orderedFaces.Select(index => centers[index]).ToArray();

            _cellRings[cell] = ring;
            _cellDirections[cell] = ring
                .Select(point => (point + normal * CellGap).Normalized()).ToArray();
            _cellNeighbors[cell] = new int[ring.Length];
            for (int edge = 0; edge < ring.Length; edge++)
                _cellNeighbors[cell][edge] = SharedNeighbour(cell, orderedFaces[edge],
                    orderedFaces[(edge + 1) % orderedFaces.Length]);
        }

        InitializeChunkStreaming();
    }

    private void InitializeChunkStreaming()
    {
        _terrainMaterial ??= new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = CreateBlockAtlas(),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            Roughness = 0.92f,
            Metallic = 0.0f,
            CullMode = BaseMaterial3D.CullModeEnum.Back
        };

        // Latitude bands + longitude sorting keep each group geographically
        // local, unlike raw icosphere vertex indices.
        int latitudeBands = 1 << Math.Max(0, Subdivisions - 1);
        int[] orderedCells = Enumerable.Range(0, _vertices.Count)
            .OrderByDescending(cell => Mathf.FloorToInt((_vertices[cell].Y + 1f) * latitudeBands))
            .ThenBy(cell => Mathf.Atan2(_vertices[cell].Z, _vertices[cell].X))
            .ToArray();
        int chunkCount = Mathf.CeilToInt(orderedCells.Length / (float)CellsPerChunk);
        _chunkCells = new int[chunkCount][];
        _chunkDirections = new Vector3[chunkCount];
        _chunkAngularRadii = new float[chunkCount];
        _cellToChunk = new int[_vertices.Count];
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            _chunkCells[chunk] = orderedCells.Skip(chunk * CellsPerChunk).Take(CellsPerChunk).ToArray();
            Vector3 center = Vector3.Zero;
            foreach (int cell in _chunkCells[chunk])
            {
                _cellToChunk[cell] = chunk;
                center += _vertices[cell];
            }
            _chunkDirections[chunk] = center.Normalized();
            float angularRadius = 0f;
            foreach (int cell in _chunkCells[chunk])
            {
                float dot = Mathf.Clamp(_chunkDirections[chunk].Dot(_vertices[cell]), -1f, 1f);
                angularRadius = Math.Max(angularRadius, Mathf.Acos(dot));
            }
            _chunkAngularRadii[chunk] = angularRadius;
        }

        _pendingChunks.Clear();
        _generatedChunks.Clear();
        _chunkLods.Clear();
        _dirtyChunks.Clear();
        GenerationProgress = 0f;
    }

    private void RefreshStreaming(Vector3 targetDirection)
    {
        _pendingChunks.Clear();
        var requests = new List<(int Chunk, int Lod, float Distance)>();
        for (int chunk = 0; chunk < _chunkCells.Length; chunk++)
        {
            float dot = Mathf.Clamp(targetDirection.Dot(_chunkDirections[chunk]), -1f, 1f);
            float centerDistance = Mathf.Acos(dot) * Radius;
            float surfaceDistance = Math.Max(0f, centerDistance - _chunkAngularRadii[chunk] * Radius);
            _chunkLods.TryGetValue(chunk, out int previousLod);
            bool alreadyLoaded = _generatedChunks.Contains(chunk);

            // Hysteresis prevents chunks near a threshold from alternating
            // between full, simplified and unloaded states while walking.
            int wantedLod;
            if (!alreadyLoaded)
                wantedLod = surfaceDistance <= HighDetailDistance ? 0
                    : surfaceDistance <= StreamingDistance ? 1 : -1;
            else if (previousLod == 0)
                wantedLod = surfaceDistance <= HighDetailDistance + 3f ? 0
                    : surfaceDistance <= StreamingDistance + 6f ? 1 : -1;
            else
                wantedLod = surfaceDistance <= HighDetailDistance - 2f ? 0
                    : surfaceDistance <= StreamingDistance + 6f ? 1 : -1;

            if (wantedLod < 0)
            {
                if (_generatedChunks.Remove(chunk))
                {
                    if (_chunkMeshes.Remove(chunk, out MeshInstance3D? mesh)) mesh.QueueFree();
                    if (_chunkCollisions.Remove(chunk, out CollisionShape3D? collision)) collision.QueueFree();
                    _chunkLods.Remove(chunk);
                }
                continue;
            }

            if (_dirtyChunks.Contains(chunk)
                || !_chunkLods.TryGetValue(chunk, out int currentLod) || currentLod != wantedLod)
                requests.Add((chunk, wantedLod, surfaceDistance));
        }

        foreach (var request in requests.OrderBy(request => request.Distance))
            _pendingChunks.Enqueue((request.Chunk, request.Lod));
    }

    private void RebuildChunk(int chunkIndex, int lod)
    {
        var visualTool = new SurfaceTool();
        visualTool.Begin(Mesh.PrimitiveType.Triangles);

        foreach (int cell in _chunkCells[chunkIndex])
        {
            Vector3 normal = _vertices[cell];
            Vector3[] ring = _cellRings[cell];
            Vector3[] directions = _cellDirections[cell];

            if (lod == 1)
            {
                int surfaceLayer = _cellLevels[cell];
                while (surfaceLayer >= MinimumLayer && !IsOccupied(cell, surfaceLayer)) surfaceLayer--;
                if (surfaceLayer < MinimumLayer) continue;
                float topRadius = Radius + surfaceLayer * BlockHeight;
                Color color = BlockColor(cell, surfaceLayer, normal, surfaceLayer * BlockHeight, ring.Length);
                int blockType = BlockType(cell, surfaceLayer, normal, surfaceLayer * BlockHeight);
                Vector3 center = normal * topRadius;
                for (int i = 0; i < ring.Length; i++)
                {
                    int next = (i + 1) % ring.Length;
                    AddTriangle(visualTool, center, directions[i] * topRadius,
                        directions[next] * topRadius, normal, color, blockType,
                        new Vector2(0.5f, 0.5f), PolygonUv(i, ring.Length), PolygonUv(next, ring.Length));
                    // LOD chunks used to consist of top faces only, making
                    // them look like floating lids before full streaming.
                    float skirtRadius = topRadius - BlockHeight;
                    Vector3 topA = directions[i] * topRadius;
                    Vector3 topB = directions[next] * topRadius;
                    Vector3 lowA = ring[i].Normalized() * skirtRadius;
                    Vector3 lowB = ring[next].Normalized() * skirtRadius;
                    Vector3 wallNormal = (topA + topB - normal * topRadius * 2f).Normalized();
                    AddTriangle(visualTool, topA, lowA, lowB, wallNormal, color.Darkened(0.38f), blockType,
                        Vector2.Zero, Vector2.Down, Vector2.One);
                    AddTriangle(visualTool, topA, lowB, topB, wallNormal, color.Darkened(0.38f), blockType,
                        Vector2.Zero, Vector2.One, Vector2.Right);
                }
                continue;
            }

            for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
            {
                if (!IsOccupied(cell, layer)) continue;
                float topRadius = Radius + layer * BlockHeight;
                float bottomRadius = topRadius - BlockHeight;
                float height = layer * BlockHeight;
                Color color = BlockColor(cell, layer, normal, height, ring.Length);
                int blockType = BlockType(cell, layer, normal, height);
                Color wallColor = color.Darkened(0.38f);
                Vector3 topCenter = normal * topRadius;
                Vector3 bottomCenter = normal * bottomRadius;

                for (int i = 0; i < ring.Length; i++)
                {
                    int next = (i + 1) % ring.Length;
                    Vector3 v1 = directions[i] * topRadius;
                    Vector3 v2 = directions[next] * topRadius;
                    // Indev uses an inset top with a full-width lower ring:
                    // smaller-looking hexes, bevelled joins, and no open gaps.
                    Vector3 b1 = ring[i].Normalized() * bottomRadius;
                    Vector3 b2 = ring[next].Normalized() * bottomRadius;
                    Vector3 wallNormal = (v1 + v2 - normal * topRadius * 2f).Normalized();

                    if (!IsOccupied(cell, layer + 1))
                        AddTriangle(visualTool, topCenter, v1, v2, normal, color, blockType,
                            new Vector2(0.5f, 0.5f), PolygonUv(i, ring.Length), PolygonUv(next, ring.Length));
                    if (!IsOccupied(cell, layer - 1))
                        AddTriangle(visualTool, bottomCenter, b2, b1, -normal, wallColor.Darkened(0.15f), blockType,
                            new Vector2(0.5f, 0.5f), PolygonUv(next, ring.Length), PolygonUv(i, ring.Length));
                    int neighbour = _cellNeighbors[cell][i];
                    if (neighbour < 0 || !IsOccupied(neighbour, layer))
                    {
                        AddTriangle(visualTool, v1, b1, b2, wallNormal, wallColor, blockType,
                            new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
                        AddTriangle(visualTool, v1, b2, v2, wallNormal, wallColor, blockType,
                            new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0f));
                    }
                }
            }
        }

        visualTool.Index();
        ArrayMesh mesh = visualTool.Commit();
        mesh.SurfaceSetMaterial(0, _terrainMaterial);
        if (!_chunkMeshes.TryGetValue(chunkIndex, out MeshInstance3D? instance))
        {
            instance = new MeshInstance3D { Name = $"TerrainChunk{chunkIndex}" };
            AddChild(instance);
            _chunkMeshes[chunkIndex] = instance;
        }
        instance.Mesh = mesh;

        if (lod == 0)
        {
            if (!_chunkCollisions.TryGetValue(chunkIndex, out CollisionShape3D? collision))
            {
                collision = new CollisionShape3D { Name = $"TerrainCollision{chunkIndex}" };
                AddChild(collision);
                _chunkCollisions[chunkIndex] = collision;
            }
            var shape = mesh.CreateTrimeshShape();
            if (shape is ConcavePolygonShape3D concave) concave.BackfaceCollision = true;
            collision.Shape = shape;
            collision.Disabled = false;
        }
        else if (_chunkCollisions.Remove(chunkIndex, out CollisionShape3D? collision))
            collision.QueueFree();
    }

    private int SharedNeighbour(int cell, int firstFaceIndex, int secondFaceIndex)
    {
        var first = _faces[firstFaceIndex];
        var second = _faces[secondFaceIndex];
        int[] firstVertices = [first.A, first.B, first.C];
        foreach (int vertex in firstVertices)
        {
            if (vertex == cell) continue;
            if (vertex == second.A || vertex == second.B || vertex == second.C)
                return vertex;
        }
        return -1;
    }

    private static Vector2 PolygonUv(int corner, int sides)
    {
        float angle = Mathf.Tau * corner / sides;
        return new Vector2(0.5f + Mathf.Cos(angle) * 0.48f, 0.5f - Mathf.Sin(angle) * 0.48f);
    }

    private static Vector2 AtlasUv(int blockType, Vector2 local)
    {
        float inset = 0.5f / BlockAtlasTileSize;
        local = new Vector2(Mathf.Lerp(inset, 1f - inset, local.X), Mathf.Lerp(inset, 1f - inset, local.Y));
        return new Vector2((Mathf.Clamp(blockType, 0, BlockCatalog.Blocks.Length - 1) + local.X) / BlockCatalog.Blocks.Length, local.Y);
    }

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 normal,
        Color color, int blockType, Vector2 uvA, Vector2 uvB, Vector2 uvC)
    {
        // Godot considers clockwise triangles front-facing. The spherical rings
        // are sorted counter-clockwise when viewed from outside, so b/c must be
        // reversed to keep the visible face pointing away from the planet.
        tool.SetNormal(normal); tool.SetColor(color); tool.SetUV(AtlasUv(blockType, uvA)); tool.AddVertex(a);
        tool.SetNormal(normal); tool.SetColor(color); tool.SetUV(AtlasUv(blockType, uvC)); tool.AddVertex(c);
        tool.SetNormal(normal); tool.SetColor(color); tool.SetUV(AtlasUv(blockType, uvB)); tool.AddVertex(b);
    }

    private static Texture2D CreateBlockAtlas()
    {
        int width = BlockAtlasTileSize * BlockCatalog.Blocks.Length;
        Image image = Image.CreateEmpty(width, BlockAtlasTileSize, false, Image.Format.Rgba8);
        for (int type = 0; type < BlockCatalog.Blocks.Length; type++)
        for (int y = 0; y < BlockAtlasTileSize; y++)
        for (int x = 0; x < BlockAtlasTileSize; x++)
        {
            uint hash = (uint)(x * 374761393 + y * 668265263 + type * 1442695041);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            uint coarse = (uint)((x / 4) * 2246822519u + (y / 4) * 3266489917u + type * 668265263u);
            coarse = (coarse ^ (coarse >> 15)) * 2246822519u;
            float grain = 0.955f + (hash & 3u) * 0.008f + (coarse & 1u) * 0.009f;
            bool darkPixel = type switch
            {
                0 => hash % 19u == 0,
                1 => hash % 11u == 0,
                2 => hash % 13u == 0 || coarse % 17u == 0,
                3 => hash % 23u == 0,
                4 => hash % 37u == 0,
                _ => hash % 9u == 0
            };
            if (darkPixel) grain -= type == 4 ? 0.025f : 0.045f;
            image.SetPixel(type * BlockAtlasTileSize + x, y, new Color(grain, grain, grain, 1f));
        }
        return ImageTexture.CreateFromImage(image);
    }

    private float GeneratedHeightAt(Vector3 direction)
    {
        float seedOffset = Seed * 0.000173f;
        if (GameSession.Current?.GenerationPreset == "Indev")
        {
            // The large planet needs several spatial frequencies. A single
            // continental wave produces kilometre-scale boring terraces, and
            // clamping it produces perfectly flat shelves.
            float indevContinental = (
                Mathf.Sin(direction.X * 5.1f + seedOffset * 13f)
              + Mathf.Sin(direction.Y * 6.3f - seedOffset * 7f)
              + Mathf.Sin(direction.Z * 7.2f + seedOffset * 19f)) / 3f;
            float hills = (
                Mathf.Sin((direction.X + direction.Z) * 23f + seedOffset * 31f)
              + Mathf.Sin((direction.Y - direction.X) * 31f - seedOffset * 17f)
              + Mathf.Sin((direction.Z - direction.Y) * 41f + seedOffset * 23f)) / 3f;
            float indevDetail = (
                Mathf.Sin(direction.X * 79f + direction.Y * 53f + seedOffset * 43f)
              + Mathf.Sin(direction.Z * 67f - direction.X * 47f - seedOffset * 29f)) / 2f;
            return 10f + indevContinental * (Relief * 0.18f)
                       + hills * (Relief * 0.32f)
                       + indevDetail * (Relief * 0.13f);
        }
        float continental = Mathf.Sin(direction.X * 4.7f + seedOffset * 13f)
                          + Mathf.Sin(direction.Y * 5.3f - seedOffset * 7f)
                          + Mathf.Sin(direction.Z * 6.1f + seedOffset * 19f);
        float detail = Mathf.Sin((direction.X + direction.Z) * 15f + seedOffset * 31f) * 0.28f;
        float shaped = Mathf.Clamp(continental / 3f + detail, -1f, 1f);
        return shaped < -0.22f ? -0.6f : shaped * Relief;
    }

    private void GenerateCaves()
    {
        _removedVoxels.Clear();
        float seedOffset = Seed * 0.00137f;
        bool indev = GameSession.Current?.GenerationPreset == "Indev";
        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            Vector3 p = _vertices[cell];
            int caveCeiling = indev ? _cellLevels[cell] - 3 : _cellLevels[cell] - 1;
            for (int layer = MinimumLayer + 1; layer <= caveCeiling; layer++)
            {
                // Low frequencies form connected chambers. A second, thinner
                // field cuts winding tunnels between those larger volumes.
                float chamber = Mathf.Sin(p.X * 10.5f + layer * 0.48f + seedOffset)
                              + Mathf.Sin(p.Y * 12.0f - layer * 0.41f - seedOffset * 2f)
                              + Mathf.Sin(p.Z * 9.0f + layer * 0.36f + seedOffset * 3f);
                float tunnelField = Mathf.Sin(p.X * 21f + p.Z * 13f + layer * 0.72f + seedOffset * 5f)
                                  + Mathf.Sin(p.Y * 18f - p.X * 11f - layer * 0.61f);
                bool largeCave = chamber > 0.62f;
                bool connectingTunnel = Mathf.Abs(tunnelField) < 0.16f && chamber > -0.55f;
                if (largeCave || connectingTunnel)
                    _removedVoxels.Add(VoxelKey(cell, layer));
            }

            // Roughly one cell in 850 becomes a natural entrance. The shaft is
            // extended down to the first cave, guaranteeing a connected opening.
            uint entranceHash = (uint)cell * 73856093u ^ (uint)Seed * 19349663u;
            entranceHash ^= entranceHash >> 13;
            entranceHash *= 1274126177u;
            bool awayFromSpawn = p.Dot(Vector3.Up) < 0.88f;
            uint entranceRarity = indev ? 16000u : 850u;
            if (awayFromSpawn && entranceHash % entranceRarity == 0u)
            {
                int surfaceLayer = _cellLevels[cell];
                int caveLayer = surfaceLayer - 1;
                while (caveLayer > MinimumLayer + 1 && IsOccupied(cell, caveLayer))
                    caveLayer--;
                for (int layer = surfaceLayer; layer >= caveLayer; layer--)
                    _removedVoxels.Add(VoxelKey(cell, layer));
            }
        }
    }

    private bool IsOccupied(int cell, int layer)
    {
        return layer >= MinimumLayer
            && layer <= _cellLevels[cell]
            && !_removedVoxels.Contains(VoxelKey(cell, layer));
    }

    private static long VoxelKey(int cell, int layer)
    {
        return ((long)cell << 32) | (uint)layer;
    }

    public void CaptureVoxelChanges(WorldData world)
    {
        world.RemovedVoxels = [.. _playerRemovedVoxels];
        world.PlacedVoxels = [.. _playerPlacedVoxels];
        world.PlacedVoxelTypes = _placedVoxelTypes.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private void ApplySavedVoxelChanges()
    {
        var world = GameSession.Current;
        if (world == null) return;
        foreach (long key in world.RemovedVoxels)
        {
            _removedVoxels.Add(key);
            _playerRemovedVoxels.Add(key);
        }
        foreach (long key in world.PlacedVoxels)
        {
            int cell = (int)(key >> 32);
            int layer = unchecked((int)(uint)key);
            if (cell < 0 || cell >= _cellLevels.Length) continue;
            _removedVoxels.Remove(key);
            _playerPlacedVoxels.Add(key);
            _cellLevels[cell] = Math.Max(_cellLevels[cell], layer);
        }
        foreach (var pair in world.PlacedVoxelTypes) _placedVoxelTypes[pair.Key] = pair.Value;
    }

    private int ClosestCell(Vector3 direction)
    {
        if (_vertices.Count == 0) return 0;
        int current = Mathf.Clamp(_closestCellHint, 0, _vertices.Count - 1);
        float currentDot = direction.Dot(_vertices[current]);
        // The icosphere graph is convex for this dot-product search. Walking
        // uphill reaches the global nearest cell without scanning 655k cells.
        for (int step = 0; step < _vertices.Count; step++)
        {
            int best = current;
            float bestDot = currentDot;
            foreach (int neighbour in _cellNeighbors[current])
            {
                if (neighbour < 0) continue;
                float dot = direction.Dot(_vertices[neighbour]);
                if (dot > bestDot) { best = neighbour; bestDot = dot; }
            }
            if (best == current) break;
            current = best;
            currentDot = bestDot;
        }
        _closestCellHint = current;
        return current;
    }

    public Vector3[] GenerateTreeSites(int maximum)
    {
        var candidates = new List<(uint Hash, Vector3 Position)>();
        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            int layer = _cellLevels[cell];
            float height = layer * BlockHeight;
            Vector3 direction = _vertices[cell];
            if (!IsOccupied(cell, layer) || NaturalBlockType(direction, height) != 0) continue;
            uint hash = (uint)(cell * 747796405 + Seed * 2891336453L);
            hash = (hash ^ (hash >> 16)) * 2246822519u;
            if (hash % 97u > 7u) continue;
            candidates.Add((hash, direction * (Radius + height)));
        }
        return candidates.OrderBy(candidate => candidate.Hash).Take(maximum)
            .Select(candidate => candidate.Position).ToArray();
    }

    private Color BiomeColor(Vector3 direction, float height, int sides)
    {
        if (sides == 5) return new Color(0.78f, 0.3f, 0.78f);
        if (height < -0.45f) return new Color(0.05f, 0.25f, 0.55f);
        float latitude = Mathf.Abs(direction.Y);
        if (latitude > 0.78f) return new Color(0.82f, 0.9f, 0.94f);
        if (height > StoneHeightThreshold) return new Color(0.36f, 0.34f, 0.3f);
        if (latitude < 0.25f && height < 0.35f) return new Color(0.72f, 0.63f, 0.3f);
        return new Color(0.18f, 0.55f, 0.22f).Lightened(Mathf.Clamp(height * 0.06f, 0f, 0.12f));
    }

    private Color BlockColor(int cell, int layer, Vector3 direction, float height, int sides)
    {
        if (!_placedVoxelTypes.TryGetValue(VoxelKey(cell, layer), out int type))
            return BiomeColor(direction, height, sides);
        return BlockCatalog.Get(type).Color;
    }

    private int BlockType(int cell, int layer, Vector3 direction, float height)
    {
        return _placedVoxelTypes.TryGetValue(VoxelKey(cell, layer), out int type)
            ? Mathf.Clamp(type, 0, BlockCatalog.Blocks.Length - 1)
            : NaturalBlockType(direction, height);
    }

    private int NaturalBlockType(Vector3 direction, float height)
    {
        if (Mathf.Abs(direction.Y) > 0.78f) return 4;
        if (height > StoneHeightThreshold) return 2;
        if (direction.Y is > -0.25f and < 0.25f && height < 0.35f) return 3;
        return 0;
    }
}
