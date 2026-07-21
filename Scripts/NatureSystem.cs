using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HexaSphericalSandbox;

/// <summary>Streams lightweight trees and owns the one-nest-per-tree state.</summary>
public partial class NatureSystem : Node3D
{
    public enum ChopResult { Miss, NeedsAxe, Chopped }
    private const int VisibleTreeLimit = 120;
    private const int CollidableTreeLimit = 48;
    private HexPlanet _planet = null!;
    private Node3D _player = null!;
    private Vector3[] _treePositions = [];
    private bool[] _occupied = [];
    private readonly Dictionary<int, Node3D> _nests = [];
    private MultiMesh _trunks = null!;
    private MultiMesh _crowns = null!;
    private MultiMesh _twigs = null!;
    private PackedScene _nestScene = null!;
    private readonly List<(StaticBody3D Body, CollisionShape3D Trunk, CollisionShape3D Crown)> _treeColliders = [];
    private float _streamTick;
    private readonly HashSet<int> _destroyed = [];
    private readonly HashSet<int> _collectedTwigs = [];
    private readonly Dictionary<int, float> _twigRespawnCooldowns = [];
    private HotbarInventory _inventory = null!;
    public int NestCount => _nests.Count;
    public int AvailableTreeCount => _occupied.Length - _occupied.Count(value => value) - _destroyed.Count;

    public override void _Ready()
    {
        _planet = GetNode<HexPlanet>("../Planet");
        _player = GetNode<Node3D>("../Player");
        _inventory = GetNode<HotbarInventory>("../InventoryUI");
        _treePositions = _planet.GenerateTreeSites(1400);
        _occupied = new bool[_treePositions.Length];
        _nestScene = GD.Load<PackedScene>("res://Models/Mobs/PolyPizza/bird_nest_poly_google.glb");

        _trunks = CreateForestMesh(new CylinderMesh
        {
            TopRadius = 0.25f, BottomRadius = 0.38f, Height = 2.8f, RadialSegments = 6,
            Material = Material(new Color(0.25f, 0.12f, 0.045f))
        }, "TreeTrunks");
        _crowns = CreateForestMesh(new CylinderMesh
        {
            TopRadius = 1.08f, BottomRadius = 1.65f, Height = 2.45f, RadialSegments = 6,
            Material = Material(new Color(0.08f, 0.38f, 0.12f))
        }, "TreeCrowns");
        _twigs = CreateForestMesh(new CylinderMesh
        {
            TopRadius = 0.045f, BottomRadius = 0.055f, Height = 0.48f, RadialSegments = 6,
            Material = Material(new Color(0.4f, 0.21f, 0.07f))
        }, "FallenTwigs");
        foreach (int tree in GameSession.Current?.DestroyedTrees ?? []) _destroyed.Add(tree);
        // Legacy saves treated a collected twig as gone forever. Keep the
        // unavailable state, but migrate it to a short regrowth cooldown.
        foreach (int tree in GameSession.Current?.CollectedTwigs ?? [])
        {
            _collectedTwigs.Add(tree);
            _twigRespawnCooldowns[tree] = (float)GD.RandRange(3.0, 12.0);
        }
        CreateTreeColliderPool();
        int[] savedNests = GameSession.Current is { } world ? [.. world.OccupiedTreeNests] : [];
        foreach (int tree in savedNests) CreateNest(tree);
        StreamTrees();
    }

    public override void _Process(double delta)
    {
        float elapsed = (float)delta;
        UpdateTwigRespawns(elapsed);
        _streamTick -= elapsed;
        if (_streamTick > 0f) return;
        _streamTick = 0.4f;
        StreamTrees();
        CollectNearbyTwig();
        foreach (var pair in _nests)
            pair.Value.Visible = pair.Value.GlobalPosition.DistanceTo(_player.GlobalPosition) < 120f;
    }

    public int FindNearestAvailableTree(Vector3 position, float maximumDistance)
        => FindNearest(position, maximumDistance, occupied: false);

    public int FindNearestNest(Vector3 position, float maximumDistance = 160f)
        => FindNearest(position, maximumDistance, occupied: true);

    public bool IsAvailable(int tree) => tree >= 0 && tree < _occupied.Length
        && !_occupied[tree] && !_destroyed.Contains(tree);
    public bool HasNest(int tree) => tree >= 0 && tree < _occupied.Length && _occupied[tree];

    public ChopResult TryChop(Vector3 origin, Vector3 direction, bool hasAxe, out Vector3 dropPosition)
    {
        direction = direction.Normalized();
        int best = -1;
        float bestAlong = 6.5f;
        for (int tree = 0; tree < _treePositions.Length; tree++)
        {
            if (_destroyed.Contains(tree)) continue;
            Vector3 trunk = _treePositions[tree] + _treePositions[tree].Normalized() * 1.5f;
            Vector3 toTree = trunk - origin;
            float along = toTree.Dot(direction);
            if (along < 0.1f || along >= bestAlong) continue;
            if ((toTree - direction * along).Length() > 0.72f * TreeScale(tree)) continue;
            best = tree; bestAlong = along;
        }
        if (best < 0) { dropPosition = Vector3.Zero; return ChopResult.Miss; }
        dropPosition = _treePositions[best] + _treePositions[best].Normalized() * 0.3f;
        if (!hasAxe) return ChopResult.NeedsAxe;
        _destroyed.Add(best);
        if (HasNest(best)) DestroyNest(best);
        if (GameSession.Current is { } world && !world.DestroyedTrees.Contains(best)) world.DestroyedTrees.Add(best);
        StreamTrees();
        SoundManager.Play(SoundKind.Tree);
        return ChopResult.Chopped;
    }

    public Vector3 NestPosition(int tree)
    {
        int index = Mathf.Clamp(tree, 0, _treePositions.Length - 1);
        Vector3 surface = _treePositions[index];
        return surface + surface.Normalized() * (4.62f * TreeScale(index));
    }

    public bool CreateNest(int tree)
    {
        if (!IsAvailable(tree)) return false;
        Vector3 position = NestPosition(tree);
        Vector3 up = position.Normalized();
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        var nest = _nestScene.Instantiate<Node3D>();
        nest.Name = $"StarlingNest{tree}";
        nest.GlobalTransform = new Transform3D(AlignedBasis(up, tangent).Scaled(Vector3.One * 0.42f), position);
        AddChild(nest);
        _nests[tree] = nest;
        _occupied[tree] = true;
        if (GameSession.Current is { } world && !world.OccupiedTreeNests.Contains(tree))
            world.OccupiedTreeNests.Add(tree);
        return true;
    }

    public bool DestroyNest(int tree)
    {
        if (!HasNest(tree)) return false;
        if (_nests.Remove(tree, out Node3D? nest)) nest.QueueFree();
        _occupied[tree] = false;
        GameSession.Current?.OccupiedTreeNests.Remove(tree);
        return true;
    }

    private int FindNearest(Vector3 position, float maximumDistance, bool occupied)
    {
        int best = -1;
        float bestSquared = maximumDistance * maximumDistance;
        for (int tree = 0; tree < _treePositions.Length; tree++)
        {
            if (_destroyed.Contains(tree)) continue;
            if (_occupied[tree] != occupied) continue;
            float distance = position.DistanceSquaredTo(NestPosition(tree));
            if (distance >= bestSquared) continue;
            bestSquared = distance;
            best = tree;
        }
        return best;
    }

    private void StreamTrees()
    {
        var nearest = new List<(float Distance, int Tree)>();
        for (int tree = 0; tree < _treePositions.Length; tree++)
        {
            if (_destroyed.Contains(tree)) continue;
            float distance = _treePositions[tree].DistanceSquaredTo(_player.GlobalPosition);
            if (distance < 125f * 125f) nearest.Add((distance, tree));
        }
        nearest.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        for (int slot = 0; slot < VisibleTreeLimit; slot++)
        {
            if (slot >= nearest.Count)
            {
                Transform3D hidden = new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);
                _trunks.SetInstanceTransform(slot, hidden);
                _crowns.SetInstanceTransform(slot, hidden);
                _twigs.SetInstanceTransform(slot, hidden);
                continue;
            }
            int tree = nearest[slot].Tree;
            Vector3 surface = _treePositions[tree];
            Vector3 up = surface.Normalized();
            Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Basis basis = AlignedBasis(up, tangent);
            float scale = TreeScale(tree);
            _trunks.SetInstanceTransform(slot, new Transform3D(basis.Scaled(Vector3.One * scale), surface + up * 1.4f * scale));
            _crowns.SetInstanceTransform(slot, new Transform3D(basis.Scaled(new Vector3(scale, scale, scale)), surface + up * 3.25f * scale));
            bool hasTwig = HasFallenTwig(tree) && !_collectedTwigs.Contains(tree);
            Transform3D twigTransform = hasTwig
                ? new Transform3D(basis.Rotated(up, 0.7f).Scaled(Vector3.One * 0.72f), surface + up * 0.14f + tangent * 0.68f)
                : new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);
            _twigs.SetInstanceTransform(slot, twigTransform);

            if (slot < CollidableTreeLimit)
                PositionTreeCollider(slot, surface, basis, scale);
        }
        for (int slot = Math.Min(nearest.Count, CollidableTreeLimit); slot < CollidableTreeLimit; slot++)
            SetTreeColliderEnabled(slot, false);
    }

    private void CollectNearbyTwig()
    {
        if (GameSession.IsCreative) return;
        for (int tree = 0; tree < _treePositions.Length; tree++)
        {
            if (!HasFallenTwig(tree) || _collectedTwigs.Contains(tree) || _destroyed.Contains(tree)) continue;
            Vector3 surface = _treePositions[tree];
            Vector3 up = surface.Normalized();
            Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 position = surface + up * 0.14f + tangent * 0.68f;
            if (position.DistanceTo(_player.GlobalPosition) > 1.05f) continue;
            _collectedTwigs.Add(tree);
            // Wind and ordinary tree growth will drop another small piece.
            // The cooldown prevents farming one tree every frame while still
            // making primitive progression easy to begin.
            _twigRespawnCooldowns[tree] = (float)GD.RandRange(25.0, 55.0);
            if (GameSession.Current is { } world && !world.CollectedTwigs.Contains(tree)) world.CollectedTwigs.Add(tree);
            _inventory.AddItem("Twig");
            SoundManager.Play(SoundKind.Pickup, -11f);
            break;
        }
    }

    private void UpdateTwigRespawns(float delta)
    {
        if (_twigRespawnCooldowns.Count == 0) return;
        List<int> ready = [];
        foreach ((int tree, float remaining) in _twigRespawnCooldowns.ToArray())
        {
            float next = remaining - delta;
            if (next <= 0f) ready.Add(tree);
            else _twigRespawnCooldowns[tree] = next;
        }
        foreach (int tree in ready)
        {
            _twigRespawnCooldowns.Remove(tree);
            _collectedTwigs.Remove(tree);
            GameSession.Current?.CollectedTwigs.Remove(tree);
        }
    }

    private void CreateTreeColliderPool()
    {
        for (int slot = 0; slot < CollidableTreeLimit; slot++)
        {
            var body = new StaticBody3D { Name = $"TreeCollision{slot}" };
            var trunk = new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = 0.38f, Height = 2.8f },
                Position = Vector3.Up * 1.4f
            };
            var crown = new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = 1.62f, Height = 2.4f },
                Position = Vector3.Up * 3.25f
            };
            body.AddChild(trunk);
            body.AddChild(crown);
            AddChild(body);
            _treeColliders.Add((body, trunk, crown));
        }
    }

    private void PositionTreeCollider(int slot, Vector3 surface, Basis basis, float scale)
    {
        var collider = _treeColliders[slot];
        collider.Body.GlobalTransform = new Transform3D(basis.Scaled(Vector3.One * scale), surface);
        collider.Trunk.Disabled = false;
        collider.Crown.Disabled = false;
    }

    private void SetTreeColliderEnabled(int slot, bool enabled)
    {
        var collider = _treeColliders[slot];
        collider.Trunk.Disabled = !enabled;
        collider.Crown.Disabled = !enabled;
    }

    private MultiMesh CreateForestMesh(PrimitiveMesh mesh, string name)
    {
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = VisibleTreeLimit };
        AddChild(new MultiMeshInstance3D { Name = name, Multimesh = multi });
        return multi;
    }

    private static float TreeScale(int tree) => 0.78f + (tree % 7) * 0.055f;
    // Stable per-world-tree eligibility: eight out of every ten tree hashes.
    private static bool HasFallenTwig(int tree)
        => (uint)unchecked(tree * 1103515245 + 12345) % 10u < 8u;

    public float TwigEligibleRatioForValidation(int sampleCount = 100)
    {
        int samples = Math.Max(1, Math.Min(sampleCount, _treePositions.Length));
        int eligible = 0;
        for (int tree = 0; tree < samples; tree++) if (HasFallenTwig(tree)) eligible++;
        return eligible / (float)samples;
    }

    public bool ValidateTwigRegrowth()
    {
        int tree = -1;
        for (int candidate = 0; candidate < _treePositions.Length; candidate++)
            if (HasFallenTwig(candidate) && !_destroyed.Contains(candidate)) { tree = candidate; break; }
        if (tree < 0) return false;
        _collectedTwigs.Add(tree);
        _twigRespawnCooldowns[tree] = 0.01f;
        if (GameSession.Current is { } world && !world.CollectedTwigs.Contains(tree)) world.CollectedTwigs.Add(tree);
        UpdateTwigRespawns(0.02f);
        return !_collectedTwigs.Contains(tree) && GameSession.Current?.CollectedTwigs.Contains(tree) == false;
    }

    private static Basis AlignedBasis(Vector3 up, Vector3 tangent)
        => new(tangent, up, tangent.Cross(up).Normalized());

    private static StandardMaterial3D Material(Color color) => new()
    {
        AlbedoColor = color, Roughness = 0.94f
    };
}
