using Godot;
using System;
using System.Collections.Generic;

namespace HexaSphericalSandbox;

public partial class SurvivalSystem : Node3D
{
    private sealed record Pickup(Node3D Node, string Item, int Amount);
    private readonly List<Pickup> _pickups = [];
    private readonly List<Node3D> _campfires = [];
    private readonly List<Node3D> _beds = [];
    private SphericalPlayer _player = null!;
    private HotbarInventory _inventory = null!;
    private HexPlanet _planet = null!;
    private Label _statusLabel = null!;
    private float _survivalTick;
    public int PickupCount => _pickups.Count;
    public int CampfireCount => _campfires.Count;
    public int BedCount => _beds.Count;

    public override void _Ready()
    {
        _player = GetNode<SphericalPlayer>("../Player");
        _inventory = GetNode<HotbarInventory>("../InventoryUI");
        _planet = GetNode<HexPlanet>("../Planet");
        _statusLabel = new Label
        {
            Name = "SurvivalStatusLabel", Position = new Vector2(18, 670),
            Size = new Vector2(420, 32)
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 18);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.9f, 0.3f));
        GetNode<CanvasLayer>("../HUD").AddChild(_statusLabel);
        foreach (float[] saved in GameSession.Current?.Campfires ?? [])
            if (saved.Length == 3) CreateCampfireVisual(new Vector3(saved[0], saved[1], saved[2]));
        foreach (float[] saved in GameSession.Current?.Beds ?? [])
            if (saved.Length == 3) CreateBedVisual(new Vector3(saved[0], saved[1], saved[2]));
        RefreshHud();
    }

    public override void _Process(double deltaValue)
    {
        float delta = (float)deltaValue;
        CollectPickups(delta);
        if (GameSession.IsCreative || GameSession.Current is not { } world)
        {
            _statusLabel.Visible = false;
            return;
        }
        _statusLabel.Visible = world.FoodPoisoned;
        _survivalTick += delta;
        if (_survivalTick >= 1f)
        {
            _survivalTick -= 1f;
            if (world.FoodPoisoned) _player.ApplyDamage(2.2f);
            RefreshHud();
        }
    }

    public bool UseSelected(Vector3 rayOrigin, Vector3 rayDirection)
    {
        string item = _inventory.SelectedItem;
        if (item == "Campfire")
        {
            if (!_planet.TryGetSurfaceObjectPlacement(rayOrigin, rayDirection,
                out Vector3 position, 0.05f, 0.65f)) return false;
            if (!_inventory.ConsumeSelected()) return true;
            CreateCampfireVisual(position);
            GameSession.Current?.Campfires.Add([position.X, position.Y, position.Z]);
            SoundManager.Play(SoundKind.BlockPlace);
            return true;
        }
        if (item == "Bed")
        {
            if (!_planet.TryGetSurfaceObjectPlacement(rayOrigin, rayDirection,
                out Vector3 position, 0.08f, 0.72f)) return false;
            if (!_inventory.ConsumeSelected()) return true;
            CreateBedVisual(position);
            GameSession.Current?.Beds.Add(ToSavePosition(position));
            SoundManager.Play(SoundKind.BlockPlace);
            return true;
        }
        if (item is "Raw Beef" or "Raw Chicken")
        {
            if (NearestCampfireDistance() <= 3.2f)
            {
                if (_inventory.ConsumeSelected())
                    _inventory.AddItem(item == "Raw Beef" ? "Cooked Beef" : "Cooked Chicken");
                SoundManager.Play(SoundKind.Craft);
            }
            else if (_inventory.ConsumeSelected())
            {
                WorldData world = GameSession.Current!;
                world.FoodPoisoned = true;
            }
            RefreshHud();
            return true;
        }
        if (item is "Cooked Beef" or "Cooked Chicken")
        {
            if (_player.Health < 100f && _inventory.ConsumeSelected())
                _player.RestoreHealth(item == "Cooked Beef" ? 38f : 28f);
            RefreshHud();
            return true;
        }
        return false;
    }

    public bool TryInteract(Vector3 origin, Vector3 direction)
    {
        Node3D? bed = FindTarget(_beds, origin, direction, 0.9f, 5.2f);
        if (bed == null || GameSession.Current is not { } world) return false;
        world.RespawnBedPosition = ToSavePosition(bed.GlobalPosition);
        GetNode<Main>("..").RequestSleep();
        SoundManager.Play(SoundKind.Craft, -13f);
        return true;
    }

    public bool TryBreakPlacedObject(Vector3 origin, Vector3 direction)
    {
        Node3D? campfire = FindTarget(_campfires, origin, direction, 0.72f, 5.2f);
        Node3D? bed = FindTarget(_beds, origin, direction, 0.9f, 5.2f);
        Node3D? target = NearestAlongRay(origin, direction, campfire, bed);
        if (target == null) return false;
        bool isBed = _beds.Contains(target);
        string item = isBed ? "Bed" : "Campfire";
        // A full inventory leaves the object intact. This avoids both loss and
        // duplication and uses the inventory's normal merge/first-empty policy.
        if (!_inventory.AddItem(item)) return true;

        List<Node3D> collection = isBed ? _beds : _campfires;
        collection.Remove(target);
        if (GameSession.Current is { } world)
        {
            List<float[]> saved = isBed ? world.Beds : world.Campfires;
            RemoveSavedPosition(saved, target.GlobalPosition);
            if (isBed && IsSamePosition(world.RespawnBedPosition, target.GlobalPosition))
                world.RespawnBedPosition = [];
        }
        target.ProcessMode = ProcessModeEnum.Disabled;
        target.QueueFree();
        SoundManager.Play(SoundKind.BlockBreak);
        return true;
    }

    public bool TryGetSafeBedRespawn(out Vector3 position)
    {
        position = Vector3.Zero;
        if (GameSession.Current?.RespawnBedPosition is not { Length: 3 } saved) return false;
        Vector3 bedPosition = new(saved[0], saved[1], saved[2]);
        Node3D? bed = null;
        foreach (Node3D candidate in _beds)
            if (IsInstanceValid(candidate) && candidate.GlobalPosition.DistanceTo(bedPosition) < 0.35f)
            { bed = candidate; break; }
        if (bed == null) return false;
        Vector3 up = bedPosition.Normalized();
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 safeDirection = (up + tangent * 0.016f).Normalized();
        position = safeDirection * (_planet.SurfaceRadius(safeDirection) + _player.GroundClearance + 0.18f);
        float feet = position.Length() - _player.GroundClearance;
        return _planet.HasRoom(safeDirection, feet, _player.BodyHeight);
    }

    public void SpawnPickup(string item, int amount, Vector3 position)
    {
        Vector3 up = position.Normalized();
        PrimitiveMesh mesh = item is "Pebble" or "Wool"
            ? new SphereMesh { Radius = 0.13f, Height = 0.22f, RadialSegments = 7, Rings = 3 }
            : new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.08f, Height = 0.48f, RadialSegments = 6 };
        mesh.Material = new StandardMaterial3D { AlbedoColor = ItemColor(item), Roughness = 0.9f };
        var node = new MeshInstance3D { Name = $"Pickup_{item}", Mesh = mesh };
        AddChild(node);
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        node.GlobalTransform = new Transform3D(new Basis(tangent, up, tangent.Cross(up)).Orthonormalized(), position + up * 0.18f);
        _pickups.Add(new Pickup(node, item, amount));
    }

    public void DropOnDeath()
    {
        Vector3 origin = _player.GlobalPosition;
        Vector3 up = origin.Normalized();
        int index = 0;
        _inventory.DropSomeItems((item, amount) =>
        {
            Vector3 tangent = up.Cross(index++ % 2 == 0 ? Vector3.Right : Vector3.Forward).Normalized();
            SpawnPickup(item, amount, origin + tangent * (0.5f + index * 0.12f));
        });
    }

    private void CollectPickups(float delta)
    {
        for (int i = _pickups.Count - 1; i >= 0; i--)
        {
            Pickup pickup = _pickups[i];
            if (!IsInstanceValid(pickup.Node)) { _pickups.RemoveAt(i); continue; }
            pickup.Node.RotateObjectLocal(Vector3.Up, delta * 1.8f);
            if (pickup.Node.GlobalPosition.DistanceTo(_player.GlobalPosition) > 1.05f) continue;
            _inventory.AddItem(pickup.Item, pickup.Amount);
            SoundManager.Play(SoundKind.Pickup, -11f);
            pickup.Node.QueueFree();
            _pickups.RemoveAt(i);
        }
    }

    private void CreateCampfireVisual(Vector3 position)
    {
        Vector3 up = position.Normalized();
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        var campfire = new Node3D { Name = "Campfire" };
        AddChild(campfire);
        campfire.GlobalTransform = new Transform3D(new Basis(tangent, up, tangent.Cross(up)).Orthonormalized(), position);
        for (int log = 0; log < 4; log++)
        {
            var mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.11f, Height = 0.75f, RadialSegments = 6,
                Material = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.12f, 0.035f) } };
            campfire.AddChild(new MeshInstance3D { Mesh = mesh, Rotation = new Vector3(Mathf.Pi / 2f, log * Mathf.Pi / 4f, 0), Position = Vector3.Up * 0.16f });
        }
        var flame = new OmniLight3D { LightColor = new Color(1f, 0.42f, 0.12f), LightEnergy = 2.2f, OmniRange = 5f, Position = Vector3.Up * 0.55f };
        campfire.AddChild(flame);
        _campfires.Add(campfire);
    }

    private void CreateBedVisual(Vector3 position)
    {
        Vector3 up = position.Normalized();
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        var bed = new Node3D { Name = "Bed" };
        AddChild(bed);
        bed.GlobalTransform = new Transform3D(new Basis(tangent, up, tangent.Cross(up)).Orthonormalized(), position);
        var frameMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.16f, 0.055f), Roughness = 0.9f };
        var blanketMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.08f, 0.07f), Roughness = 0.95f };
        bed.AddChild(new MeshInstance3D
        {
            Name = "Frame", Mesh = new BoxMesh { Size = new Vector3(1.15f, 0.18f, 2.0f), Material = frameMaterial },
            Position = Vector3.Up * 0.24f
        });
        bed.AddChild(new MeshInstance3D
        {
            Name = "Mattress", Mesh = new BoxMesh { Size = new Vector3(1.05f, 0.22f, 1.9f), Material = blanketMaterial },
            Position = Vector3.Up * 0.43f
        });
        _beds.Add(bed);
    }

    private Node3D? FindTarget(List<Node3D> objects, Vector3 origin, Vector3 direction, float radius, float range)
    {
        direction = direction.Normalized();
        float terrainDistance = _planet.GetRayHitDistance(origin, direction, range);
        Node3D? best = null;
        float nearest = Math.Min(range, terrainDistance + 0.05f);
        foreach (Node3D candidate in objects)
        {
            if (!IsInstanceValid(candidate)) continue;
            Vector3 toObject = candidate.GlobalPosition + candidate.GlobalPosition.Normalized() * 0.35f - origin;
            float along = toObject.Dot(direction);
            if (along < 0f || along >= nearest || (toObject - direction * along).Length() > radius) continue;
            nearest = along;
            best = candidate;
        }
        return best;
    }

    private static Node3D? NearestAlongRay(Vector3 origin, Vector3 direction, Node3D? first, Node3D? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        direction = direction.Normalized();
        return (first.GlobalPosition - origin).Dot(direction) <= (second.GlobalPosition - origin).Dot(direction) ? first : second;
    }

    private static float[] ToSavePosition(Vector3 position) => [position.X, position.Y, position.Z];

    private static bool IsSamePosition(float[] saved, Vector3 position)
        => saved.Length == 3 && new Vector3(saved[0], saved[1], saved[2]).DistanceTo(position) < 0.35f;

    private static void RemoveSavedPosition(List<float[]> saved, Vector3 position)
    {
        for (int index = saved.Count - 1; index >= 0; index--)
            if (IsSamePosition(saved[index], position)) { saved.RemoveAt(index); return; }
    }

    private float NearestCampfireDistance()
    {
        float nearest = float.MaxValue;
        foreach (Node3D campfire in _campfires)
            if (IsInstanceValid(campfire)) nearest = Math.Min(nearest, campfire.GlobalPosition.DistanceTo(_player.GlobalPosition));
        return nearest;
    }

    private void RefreshHud()
    {
        WorldData? world = GameSession.Current;
        if (world == null) return;
        _statusLabel.Visible = !GameSession.IsCreative && world.FoodPoisoned;
        _statusLabel.Text = world.FoodPoisoned ? "FOOD POISONING — NO KNOWN CURE" : "";
    }

    public void CureFoodPoisoning()
    {
        if (GameSession.Current is { } world) world.FoodPoisoned = false;
        RefreshHud();
    }

    private static Color ItemColor(string item) => item == "Pebble" ? new Color(0.42f, 0.44f, 0.47f)
        : item.Contains("Beef") || item.Contains("Chicken") ? new Color(0.62f, 0.16f, 0.1f)
        : new Color(0.45f, 0.25f, 0.08f);
}
