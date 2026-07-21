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
    [Export(PropertyHint.Range, "1,6,1")] public int Subdivisions { get; set; } = 5;
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
    private const int MinimumLayer = -4;
    private const int MaximumLayer = 10;
    private const int CellsPerChunk = 64;
    private readonly List<MeshInstance3D> _chunkMeshes = [];
    private StandardMaterial3D _terrainMaterial = null!;
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

    public float GenerationProgress { get; private set; }

    public override void _Ready()
    {
        if (GameSession.Current != null)
        {
            Seed = GameSession.Current.Seed;
            switch (GameSession.Current.Quality)
            {
                case "High": HighDetailDistance = 18f; StreamingDistance = 38f; break;
                case "Balanced": HighDetailDistance = 12f; StreamingDistance = 26f; break;
                default: HighDetailDistance = 9f; StreamingDistance = 20f; break;
            }
        }
        GenerateIcosphere();
        BuildHexPlanet();
        _streamingTarget = GetNode<Node3D>("../Player");
        RefreshStreaming(Vector3.Up);
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
            GenerationProgress = _generatedChunks.Count / (float)_chunkMeshes.Count;
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
        bool stone = height > 1.25f;
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
        return chunk >= 0 && chunk < _chunkMeshes.Count
            && _generatedChunks.Contains(chunk)
            && _chunkMeshes[chunk].Mesh != null;
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
        bool stone = height > 1.25f;
        bool grass = height >= -0.45f && latitude <= 0.78f
            && !(latitude < 0.25f && height < 0.35f);
        return stone || grass;
    }

    private int ClosestCellNear(Vector3 direction, int startCell)
    {
        if (startCell < 0 || startCell >= _vertices.Count) return ClosestCell(direction);
        int current = startCell;
        float currentDot = direction.Dot(_vertices[current]);
        // A walking mob crosses adjacent cells, so hill-climbing through the
        // local topology replaces a full scan of all ~10k planet cells.
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

    public bool TryEdit(Vector3 rayOrigin, Vector3 rayDirection, int levelDelta)
    {
        int bestCell = -1;
        int bestLayer = 0;
        float bestDistance = float.MaxValue;
        float selectionRadius = Radius * 0.038f;
        rayDirection = rayDirection.Normalized();

        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            Vector3 normal = _vertices[cell];
            for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
            {
                if (!IsOccupied(cell, layer)) continue;
                Vector3 center = normal * (Radius + (layer - 0.5f) * BlockHeight);
                float alongRay = (center - rayOrigin).Dot(rayDirection);
                if (alongRay < 0.15f || alongRay > 9.0f || alongRay >= bestDistance) continue;
                Vector3 offset = rayOrigin + rayDirection * alongRay - center;
                float radial = Mathf.Abs(offset.Dot(normal));
                float tangent = (offset - normal * offset.Dot(normal)).Length();
                if (radial <= BlockHeight * 0.55f && tangent <= selectionRadius)
                {
                    bestDistance = alongRay;
                    bestCell = cell;
                    bestLayer = layer;
                }
            }
        }

        if (bestCell < 0) return false;
        if (levelDelta < 0)
        {
            long key = VoxelKey(bestCell, bestLayer);
            _removedVoxels.Add(key);
            _playerRemovedVoxels.Add(key);
            _playerPlacedVoxels.Remove(key);
        }
        else
        {
            int placementLayer = Math.Min(bestLayer + 1, MaximumLayer);
            long key = VoxelKey(bestCell, placementLayer);
            _removedVoxels.Remove(key);
            _playerRemovedVoxels.Remove(key);
            _playerPlacedVoxels.Add(key);
            _cellLevels[bestCell] = Math.Max(_cellLevels[bestCell], placementLayer);
        }
        int editedChunk = _cellToChunk[bestCell];
        RebuildChunk(editedChunk, 0);
        _generatedChunks.Add(editedChunk);
        _chunkLods[editedChunk] = 0;
        _dirtyChunks.Remove(editedChunk);

        // Removing or adding a voxel also changes which side faces are exposed
        // in neighbouring cells. Rebuild those chunks progressively.
        foreach (int neighbour in _cellNeighbors[bestCell])
        {
            if (neighbour < 0) continue;
            int neighbourChunk = _cellToChunk[neighbour];
            if (neighbourChunk == editedChunk || !_generatedChunks.Contains(neighbourChunk)) continue;
            _dirtyChunks.Add(neighbourChunk);
            int lod = _chunkLods.GetValueOrDefault(neighbourChunk, 0);
            _pendingChunks.Enqueue((neighbourChunk, lod));
        }
        EmitSignal(SignalName.VoxelEdited, _vertices[bestCell]);
        return true;
    }

    public float GetRayHitDistance(Vector3 rayOrigin, Vector3 rayDirection, float maximumDistance = 18.0f)
    {
        float bestDistance = maximumDistance;
        float selectionRadius = Radius * 0.038f;
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
                    Vector3 center = normal * (Radius + (layer - 0.5f) * BlockHeight);
                    float alongRay = (center - rayOrigin).Dot(rayDirection);
                    if (alongRay < 0.05f || alongRay >= bestDistance) continue;
                    Vector3 offset = rayOrigin + rayDirection * alongRay - center;
                    float radial = Mathf.Abs(offset.Dot(normal));
                    float tangent = (offset - normal * offset.Dot(normal)).Length();
                    if (radial <= BlockHeight * 0.55f && tangent <= selectionRadius)
                        bestDistance = alongRay;
                }
            }
        }
        return bestDistance;
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
                _cellLevels[cell] = Mathf.RoundToInt(GeneratedHeightAt(_vertices[cell]) / BlockHeight);
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
            Roughness = 0.92f,
            Metallic = 0.0f,
            CullMode = BaseMaterial3D.CullModeEnum.Back
        };

        // Latitude bands + longitude sorting keep each group geographically
        // local, unlike raw icosphere vertex indices.
        int[] orderedCells = Enumerable.Range(0, _vertices.Count)
            .OrderByDescending(cell => Mathf.FloorToInt((_vertices[cell].Y + 1f) * 16f))
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

        while (_chunkMeshes.Count < chunkCount)
        {
            var instance = new MeshInstance3D { Name = $"TerrainChunk{_chunkMeshes.Count}" };
            AddChild(instance);
            _chunkMeshes.Add(instance);
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
                    _chunkMeshes[chunk].Mesh = null;
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
                Color color = BiomeColor(normal, surfaceLayer * BlockHeight, ring.Length);
                Vector3 center = normal * topRadius;
                for (int i = 0; i < ring.Length; i++)
                {
                    int next = (i + 1) % ring.Length;
                    AddTriangle(visualTool, center, directions[i] * topRadius,
                        directions[next] * topRadius, normal, color);
                }
                continue;
            }

            for (int layer = MinimumLayer; layer <= _cellLevels[cell]; layer++)
            {
                if (!IsOccupied(cell, layer)) continue;
                float topRadius = Radius + layer * BlockHeight;
                float bottomRadius = topRadius - BlockHeight;
                float height = layer * BlockHeight;
                Color color = BiomeColor(normal, height, ring.Length);
                Color wallColor = color.Darkened(0.38f);
                Vector3 topCenter = normal * topRadius;
                Vector3 bottomCenter = normal * bottomRadius;

                for (int i = 0; i < ring.Length; i++)
                {
                    int next = (i + 1) % ring.Length;
                    Vector3 v1 = directions[i] * topRadius;
                    Vector3 v2 = directions[next] * topRadius;
                    Vector3 b1 = directions[i] * bottomRadius;
                    Vector3 b2 = directions[next] * bottomRadius;
                    Vector3 wallNormal = (v1 + v2 - normal * topRadius * 2f).Normalized();

                    if (!IsOccupied(cell, layer + 1))
                        AddTriangle(visualTool, topCenter, v1, v2, normal, color);
                    if (!IsOccupied(cell, layer - 1))
                        AddTriangle(visualTool, bottomCenter, b2, b1, -normal, wallColor.Darkened(0.15f));
                    int neighbour = _cellNeighbors[cell][i];
                    if (neighbour < 0 || !IsOccupied(neighbour, layer))
                    {
                        AddTriangle(visualTool, v1, b1, b2, wallNormal, wallColor);
                        AddTriangle(visualTool, v1, b2, v2, wallNormal, wallColor);
                    }
                }
            }
        }

        visualTool.Index();
        ArrayMesh mesh = visualTool.Commit();
        mesh.SurfaceSetMaterial(0, _terrainMaterial);
        _chunkMeshes[chunkIndex].Mesh = mesh;
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

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color color)
    {
        // Godot considers clockwise triangles front-facing. The spherical rings
        // are sorted counter-clockwise when viewed from outside, so b/c must be
        // reversed to keep the visible face pointing away from the planet.
        tool.SetNormal(normal); tool.SetColor(color); tool.AddVertex(a);
        tool.SetNormal(normal); tool.SetColor(color); tool.AddVertex(c);
        tool.SetNormal(normal); tool.SetColor(color); tool.AddVertex(b);
    }

    private float GeneratedHeightAt(Vector3 direction)
    {
        float seedOffset = Seed * 0.000173f;
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
        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            Vector3 p = _vertices[cell];
            for (int layer = MinimumLayer + 1; layer < _cellLevels[cell]; layer++)
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
            if (awayFromSpawn && entranceHash % 850u == 0u)
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
    }

    private int ClosestCell(Vector3 direction)
    {
        int best = 0;
        float bestDot = -2f;
        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            float dot = direction.Dot(_vertices[cell]);
            if (dot > bestDot) { bestDot = dot; best = cell; }
        }
        return best;
    }

    private static Color BiomeColor(Vector3 direction, float height, int sides)
    {
        if (sides == 5) return new Color(0.78f, 0.3f, 0.78f);
        if (height < -0.45f) return new Color(0.05f, 0.25f, 0.55f);
        float latitude = Mathf.Abs(direction.Y);
        if (latitude > 0.78f) return new Color(0.82f, 0.9f, 0.94f);
        if (height > 1.25f) return new Color(0.36f, 0.34f, 0.3f);
        if (latitude < 0.25f && height < 0.35f) return new Color(0.72f, 0.63f, 0.3f);
        return new Color(0.18f, 0.55f, 0.22f).Lightened(Mathf.Clamp(height * 0.06f, 0f, 0.12f));
    }
}
