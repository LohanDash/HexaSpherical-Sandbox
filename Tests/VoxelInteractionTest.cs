using Godot;
using System;

namespace HexaSphericalSandbox.Tests;

public partial class VoxelInteractionTest : Node
{
    public override async void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData { Id = "__voxel_test__", Seed = 73421, GenerationPreset = "PreIndev", Quality = "Low" };
            var player = new Node3D { Name = "Player" }; AddChild(player);
            var planet = new HexPlanet { Name = "Planet" }; AddChild(planet);
            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Vector3 direction = Vector3.Up;
            float originalSurface = planet.SurfaceRadius(direction);
            player.GlobalPosition = direction * (originalSurface + 4f);
            Vector3 rayOrigin = direction * (originalSurface + 5f);
            if (!planet.TryEdit(rayOrigin, -direction, -1)) throw new InvalidOperationException("Centre mining ray missed.");
            float minedSurface = planet.SurfaceRadius(direction);
            if (minedSurface >= originalSurface - 0.5f) throw new InvalidOperationException("Centre block was not mined.");
            if (!planet.TryEdit(rayOrigin, -direction, 1, 2)) throw new InvalidOperationException("Top-face placement failed.");
            if (planet.SurfaceRadius(direction) < originalSurface - 0.1f) throw new InvalidOperationException("Placed block did not restore the column.");
            player.GlobalPosition = direction * (originalSurface + 0.78f + 1.5f);
            if (!planet.TryEdit(rayOrigin, -direction, 1, 1)) throw new InvalidOperationException("Jump placement below player failed.");
            float raisedSurface = planet.SurfaceRadius(direction);
            Vector3 sideOrigin = direction * (raisedSurface - planet.BlockHeight * 0.5f) + Vector3.Right * 6f;
            if (!planet.TryEdit(sideOrigin, Vector3.Left, 1, 3)) throw new InvalidOperationException("Side-face neighbour placement failed.");
            GameSession.Current = null;
            GD.Print("VOXEL_INTERACTION_TEST: PASS"); GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null; GD.PushError("VOXEL_INTERACTION_TEST: FAIL\n" + exception); GetTree().Quit(1);
        }
    }
}
