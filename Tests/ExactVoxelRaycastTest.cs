using Godot;
using System;

namespace HexaSphericalSandbox.Tests;

public partial class ExactVoxelRaycastTest : Node
{
    public override async void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData
            {
                Id = "__exact_voxel_raycast_test__",
                Seed = 73421,
                GenerationPreset = "PreIndev",
                Quality = "Low"
            };
            var player = new Node3D { Name = "Player" };
            AddChild(player);
            player.GlobalPosition = Vector3.Up * 80f;
            var planet = new HexPlanet { Name = "Planet" };
            AddChild(planet);
            for (int i = 0; i < 18; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            ValidateRay(planet, HexPlanet.VoxelFace.Top, false, "top-face centre");
            int localTriangleTests = planet.LastRaycastTriangleTests;
            int allDetailedTriangles = planet.LoadedDetailedTriangleCount;
            if (allDetailedTriangles > 0 && localTriangleTests >= allDetailedTriangles)
                throw new InvalidOperationException("Raycast chunk-cap culling inspected the entire detailed planet.");
            ValidateRay(planet, HexPlanet.VoxelFace.Top, true, "top-face edge");
            ValidateRay(planet, HexPlanet.VoxelFace.Bottom, true, "bottom-face edge");

            if (!planet.TryGetInteractionTriangleSample(HexPlanet.VoxelFace.Top, false,
                out Vector3 objectOrigin, out Vector3 objectDirection, out HexPlanet.VoxelRayHit objectFloor)
                || !planet.TryGetSurfaceObjectPlacement(objectOrigin, objectDirection, out Vector3 objectPosition))
                throw new InvalidOperationException("Surface object placement missed the exact floor triangle.");
            Vector3 expectedObjectPosition = objectFloor.Position.Normalized() * (objectFloor.Position.Length() + 0.08f);
            if (objectPosition.DistanceTo(expectedObjectPosition) > 0.001f)
                throw new InvalidOperationException("Surface object placement escaped to the planet's outer roof surface.");

            if (!planet.TryGetInteractionTriangleSample(HexPlanet.VoxelFace.Side, true,
                out Vector3 sideOrigin, out Vector3 sideDirection, out HexPlanet.VoxelRayHit sideExpected))
                throw new InvalidOperationException("No exposed side triangle was generated.");
            if (!planet.TryGetVoxelRayHit(sideOrigin, sideDirection, out HexPlanet.VoxelRayHit sideHit))
                throw new InvalidOperationException("Side-face ray missed its source triangle.");
            AssertSameVoxel(sideExpected, sideHit, "side-face edge");
            if (sideHit.Face != HexPlanet.VoxelFace.Side || sideHit.SideEdge < 0)
                throw new InvalidOperationException("The exact side face was not preserved in the hit metadata.");
            if (!planet.TryEdit(sideOrigin, sideDirection, 1, 2))
                throw new InvalidOperationException("Side-face placement failed.");
            if (planet.LastEditedCell == sideHit.Cell || planet.LastEditedLayer != sideHit.Layer)
                throw new InvalidOperationException("Side placement did not select the touched face's neighbour.");

            if (!planet.TryGetInteractionTriangleSample(HexaSphericalSandbox.HexPlanet.VoxelFace.Top, true,
                out Vector3 breakOrigin, out Vector3 breakDirection, out HexPlanet.VoxelRayHit breakExpected))
                throw new InvalidOperationException("No top triangle was available for mining.");
            if (!planet.TryEdit(breakOrigin, breakDirection, -1))
                throw new InvalidOperationException("Exact-edge mining failed.");
            if (planet.LastEditedCell != breakExpected.Cell || planet.LastEditedLayer != breakExpected.Layer)
                throw new InvalidOperationException("Mining edited a voxel other than the first touched triangle.");

            GameSession.Current = null;
            GD.Print($"EXACT_VOXEL_RAYCAST_TEST: PASS (local triangles={localTriangleTests}/{allDetailedTriangles})");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null;
            GD.PushError("EXACT_VOXEL_RAYCAST_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }

    private static void ValidateRay(HexPlanet planet, HexPlanet.VoxelFace face, bool nearEdge, string label)
    {
        if (!planet.TryGetInteractionTriangleSample(face, nearEdge,
            out Vector3 origin, out Vector3 direction, out HexPlanet.VoxelRayHit expected))
            throw new InvalidOperationException($"No triangle was available for {label}.");
        // 4.2 m is the actual rear/selfie camera boom. It must remain exact
        // while allowing spherical chunk-cap culling to reject farther chunks.
        if (!planet.TryGetVoxelRayHit(origin, direction, out HexPlanet.VoxelRayHit actual, 4.2f))
            throw new InvalidOperationException($"Ray missed at {label}.");
        AssertSameVoxel(expected, actual, label);
        if (actual.Face != face)
            throw new InvalidOperationException($"Wrong face at {label}: expected {face}, got {actual.Face}.");
    }

    private static void AssertSameVoxel(HexPlanet.VoxelRayHit expected,
        HexPlanet.VoxelRayHit actual, string label)
    {
        if (actual.Cell != expected.Cell || actual.Layer != expected.Layer)
            throw new InvalidOperationException(
                $"Wrong voxel at {label}: expected {expected.Cell}/{expected.Layer}, got {actual.Cell}/{actual.Layer}.");
    }
}
