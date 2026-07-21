using Godot;
using System;
using System.Collections.Generic;

namespace HexaSphericalSandbox;

public partial class SurvivalSystem : Node3D
{
    private sealed record Pickup(Node3D Node, string Item, int Amount);
    private readonly List<Pickup> _pickups = [];
    private readonly List<Node3D> _campfires = [];
    private SphericalPlayer _player = null!;
    private HotbarInventory _inventory = null!;
    private HexPlanet _planet = null!;
    private Label _statusLabel = null!;
    private float _survivalTick;

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

    public bool UseSelected(Vector3 placementDirection)
    {
        string item = _inventory.SelectedItem;
        if (item == "Campfire")
        {
            if (!_inventory.ConsumeSelected()) return true;
            Vector3 position = _planet.PassiveMobSurfacePosition(placementDirection, 0.05f);
            CreateCampfireVisual(position);
            GameSession.Current?.Campfires.Add([position.X, position.Y, position.Z]);
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

    public void SpawnPickup(string item, int amount, Vector3 position)
    {
        Vector3 up = position.Normalized();
        PrimitiveMesh mesh = item == "Pebble"
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
