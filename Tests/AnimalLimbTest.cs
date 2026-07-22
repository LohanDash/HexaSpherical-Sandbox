using Godot;
using System;

namespace HexaSphericalSandbox.Tests;

public partial class AnimalLimbTest : Node
{
    public override async void _Ready()
    {
        WorldData? testWorld = null;
        try
        {
            testWorld = new WorldData
            {
                Id = "__animal_limb_test__" + Guid.NewGuid().ToString("N"),
                Seed = 73421,
                GenerationPreset = "PreIndev",
                Quality = "Low"
            };
            GameSession.Current = testWorld;
            Main main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
            AddChild(main);
            if (!Mathf.IsEqualApprox(main.DayLengthSeconds, 1800f))
                throw new InvalidOperationException("The full day/night cycle is not 30 real-time minutes.");
            for (int frame = 0; frame < 24; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            MobManager mobs = main.GetNode<MobManager>("MobManager");
            if (!mobs.SpawnEgg("Chicken", Vector3.Up) || !mobs.SpawnEgg("Cow", Vector3.Up)
                || !mobs.SpawnEgg("Sheep", Vector3.Up))
                throw new InvalidOperationException("Test animals could not be spawned.");
            // Imported AnimationPlayers can start one or two frames after the
            // scene instance on slower machines. Wait for the actual invariant
            // instead of racing the resource importer.
            for (int frame = 0; frame < 12 && !mobs.AllMobLocomotionReady; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (!mobs.AllMobLocomotionReady)
                throw new InvalidOperationException("A real locomotion animation is not active. " + mobs.LocomotionDebug);
            if (!mobs.AllMobLimbConfigurationsValid)
                throw new InvalidOperationException("An animal has duplicated or missing legs.");

            main.QueueFree();
            for (int frame = 0; frame < 3; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            WorldStore.Delete(testWorld);
            GameSession.Current = null;
            GD.Print("ANIMAL_LIMB_TEST: PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            if (testWorld != null) WorldStore.Delete(testWorld);
            GameSession.Current = null;
            GD.PushError("ANIMAL_LIMB_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }
}
