using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HexaSphericalSandbox;

public partial class HexPlanet : StaticBody3D
{
    [Export] public float Radius { get; set; } = 18.0f;
    [Export(PropertyHint.Range, "1,5,1")] public int Subdivisions { get; set; } = 4;
    [Export] public int Seed { get; set; } = 73421;
    [Export] public float Relief { get; set; } = 2.8f;
    [Export(PropertyHint.Range, "0.0,0.18,0.01")] public float CellGap { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0.5,4.0,0.1")] public float BlockHeight { get; set; } = 2.6f;

    private readonly List<Vector3> _vertices = [];
    private readonly List<(int A, int B, int C)> _faces = [];
    private readonly Dictionary<long, int> _midpoints = [];
    private Vector3[][] _cellRings = [];
    private int[] _cellLevels = [];
    private readonly HashSet<long> _removedVoxels = [];
    private const int MinimumLayer = -4;
    private const int MaximumLayer = 10;
    private const int CellsPerChunk = 64;
    private readonly List<MeshInstance3D> _chunkMeshes = [];
    private StandardMaterial3D _terrainMaterial = null!;
    private readonly Queue<int> _pendingChunks = [];
    private readonly HashSet<int> _generatedChunks = [];

    public float GenerationProgress { get; private set; }

    public override void _Ready()
    {
        GenerateIcosphere();
        BuildHexPlanet();
    }

    public override void _Process(double delta)
    {
        while (_pendingChunks.Count > 0)
        {
            int chunk = _pendingChunks.Dequeue();
            if (_generatedChunks.Contains(chunk)) continue;
            RebuildChunk(chunk);
            _generatedChunks.Add(chunk);
            GenerationProgress = _generatedChunks.Count / (float)_chunkMeshes.Count;
            break; // One terrain chunk per rendered frame.
        }
        SetProcess(_pendingChunks.Count > 0);
    }

    public float SurfaceRadius(Vector3 direction)
    {
        int cell = ClosestCell(direction.Normalized());
        for (int layer = _cellLevels[cell]; layer >= MinimumLayer; layer--)
            if (IsOccupied(cell, layer)) return Radius + layer * BlockHeight;
        return Radius + (MinimumLayer - 1) * BlockHeight;
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
            _removedVoxels.Add(VoxelKey(bestCell, bestLayer));
        }
        else
        {
            int placementLayer = Math.Min(bestLayer + 1, MaximumLayer);
            _removedVoxels.Remove(VoxelKey(bestCell, placementLayer));
            _cellLevels[bestCell] = Math.Max(_cellLevels[bestCell], placementLayer);
        }
        int editedChunk = bestCell / CellsPerChunk;
        RebuildChunk(editedChunk);
        _generatedChunks.Add(editedChunk);
        return true;
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
        if (_cellLevels.Length != _vertices.Count)
        {
            _cellLevels = new int[_vertices.Count];
            for (int cell = 0; cell < _vertices.Count; cell++)
                _cellLevels[cell] = Mathf.RoundToInt(GeneratedHeightAt(_vertices[cell]) / BlockHeight);
            GenerateCaves();
        }

        for (int cell = 0; cell < _vertices.Count; cell++)
        {
            Vector3 normal = _vertices[cell];
            Vector3 axisX = normal.Cross(Mathf.Abs(normal.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 axisY = normal.Cross(axisX).Normalized();
            var ring = adjacentFaces[cell]
                .Select(index => centers[index])
                .OrderBy(point => Mathf.Atan2(point.Dot(axisY), point.Dot(axisX)))
                .ToArray();

            _cellRings[cell] = ring;
        }

        BeginProgressiveChunkGeneration();
    }

    private void BeginProgressiveChunkGeneration()
    {
        _terrainMaterial ??= new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.92f,
            Metallic = 0.0f,
            CullMode = BaseMaterial3D.CullModeEnum.Back
        };

        int chunkCount = Mathf.CeilToInt(_vertices.Count / (float)CellsPerChunk);
        while (_chunkMeshes.Count < chunkCount)
        {
            var instance = new MeshInstance3D { Name = $"TerrainChunk{_chunkMeshes.Count}" };
            AddChild(instance);
            _chunkMeshes.Add(instance);
        }

        _pendingChunks.Clear();
        _generatedChunks.Clear();
        GenerationProgress = 0f;

        // Generate the north/spawn side first, then progressively move toward
        // the far side of the planet.
        var orderedChunks = Enumerable.Range(0, chunkCount)
            .OrderByDescending(ChunkSpawnPriority);
        foreach (int chunk in orderedChunks)
            _pendingChunks.Enqueue(chunk);
        SetProcess(true);
    }

    private float ChunkSpawnPriority(int chunkIndex)
    {
        int firstCell = chunkIndex * CellsPerChunk;
        int lastCell = Math.Min(firstCell + CellsPerChunk, _vertices.Count);
        float priority = -1f;
        for (int cell = firstCell; cell < lastCell; cell++)
            priority = Math.Max(priority, _vertices[cell].Dot(Vector3.Up));
        return priority;
    }

    private void RebuildChunk(int chunkIndex)
    {
        var visualTool = new SurfaceTool();
        visualTool.Begin(Mesh.PrimitiveType.Triangles);

        int firstCell = chunkIndex * CellsPerChunk;
        int lastCell = Math.Min(firstCell + CellsPerChunk, _vertices.Count);
        for (int cell = firstCell; cell < lastCell; cell++)
        {
            Vector3 normal = _vertices[cell];
            Vector3[] ring = _cellRings[cell];
            var directions = ring.Select(point => (point + normal * CellGap).Normalized()).ToArray();

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
                    AddTriangle(visualTool, v1, b1, b2, wallNormal, wallColor);
                    AddTriangle(visualTool, v1, b2, v2, wallNormal, wallColor);
                }
            }
        }

        visualTool.Index();
        ArrayMesh mesh = visualTool.Commit();
        mesh.SurfaceSetMaterial(0, _terrainMaterial);
        _chunkMeshes[chunkIndex].Mesh = mesh;
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
                float cave = Mathf.Sin(p.X * 19f + layer * 1.7f + seedOffset)
                           + Mathf.Sin(p.Y * 23f - layer * 1.3f - seedOffset * 2f)
                           + Mathf.Sin(p.Z * 17f + layer * 0.9f + seedOffset * 3f);
                if (cave > 1.35f)
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
