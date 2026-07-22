using Godot;
using System;

namespace HexaSphericalSandbox.Tests;

public partial class FocusedGameplayBehaviorsTest : Node
{
    public override async void _Ready()
    {
        WorldData? world = null;
        try
        {
            world = new WorldData
            {
                Id = "__focused_behaviors__" + Guid.NewGuid().ToString("N"),
                Seed = 73421, GenerationPreset = "PreIndev", Quality = "Low", GameMode = "Survival"
            };
            GameSession.Current = world;
            Main main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
            AddChild(main);
            for (int frame = 0; frame < 24; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var player = main.GetNode<SphericalPlayer>("Player");
            var survival = main.GetNode<SurvivalSystem>("SurvivalSystem");
            var inventory = main.GetNode<HotbarInventory>("InventoryUI");
            var planet = main.GetNode<HexPlanet>("Planet");
            var mobs = main.GetNode<MobManager>("MobManager");
            var monsters = main.GetNode<NightMonsterManager>("NightMonsterManager");

            if (!player.FlashlightUsesRightHandPivot)
                throw new InvalidOperationException("Flashlight is not parented to the right-hand shoulder pivot.");
            if (!player.HeldItemUsesLeftHandPivot)
                throw new InvalidOperationException("The selected item is not parented to the left-hand shoulder pivot.");

            inventory.AddItem("Campfire");
            if (!inventory.SelectItemForValidation("Campfire") || !survival.UseSelected(Vector3.Up))
                throw new InvalidOperationException("Campfire placement failed.");
            Vector3 objectPosition = planet.PassiveMobSurfacePosition(Vector3.Up, 0.05f);
            int campfiresBefore = survival.CampfireCount;
            if (!survival.TryBreakPlacedObject(objectPosition + Vector3.Up * 3f, Vector3.Down)
                || survival.CampfireCount != campfiresBefore - 1 || inventory.CountItem("Campfire") != 1)
                throw new InvalidOperationException("Campfire was not recovered into the normal inventory.");

            inventory.AddItem("Bed");
            if (!inventory.SelectItemForValidation("Bed") || !survival.UseSelected(Vector3.Up))
                throw new InvalidOperationException("Bed placement failed.");
            Vector3 bedPosition = planet.PassiveMobSurfacePosition(Vector3.Up, 0.08f);
            if (!survival.TryInteract(bedPosition + Vector3.Up * 3f, Vector3.Down)
                || world.RespawnBedPosition.Length != 3 || main.IsGlobalNight)
                throw new InvalidOperationException("A daytime bed interaction did not only set spawn.");
            main.SetGlobalNightForValidation();
            if (!survival.TryInteract(bedPosition + Vector3.Up * 3f, Vector3.Down)
                || world.RespawnBedPosition.Length != 3 || main.IsGlobalNight)
                throw new InvalidOperationException("Bed did not set spawn and advance global night.");
            if (!survival.TryGetSafeBedRespawn(out Vector3 safeRespawn)
                || safeRespawn.Length() <= planet.SurfaceRadius(safeRespawn.Normalized()))
                throw new InvalidOperationException("Bed did not produce a safe respawn position.");
            player.ApplyDamage(200f);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Button respawn = main.GetNode<Control>("HUD/DeathMenu").FindChild("Respawn", true, false) as Button
                ?? throw new InvalidOperationException("Respawn button is missing.");
            respawn.EmitSignal(Button.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (player.IsDead || player.GlobalPosition.DistanceTo(safeRespawn) > 0.08f)
                throw new InvalidOperationException("Player did not respawn at the safe bed position.");
            if (!survival.TryBreakPlacedObject(bedPosition + Vector3.Up * 3f, Vector3.Down)
                || world.RespawnBedPosition.Length != 0)
                throw new InvalidOperationException("Destroyed bed did not invalidate its respawn point.");

            inventory.AddItem("Stone Block", 2);
            inventory.AddItem("Stick");
            if (!inventory.PlaceOneInCraftForValidation("Stone Block", 0)
                || !inventory.PlaceOneInCraftForValidation("Stone Block", 1)
                || !inventory.PlaceOneInCraftForValidation("Stick", 2)
                || inventory.CurrentRecipe() != "Shears" || !inventory.CraftOnceForValidation())
                throw new InvalidOperationException("The shapeless shears recipe failed.");
            if (!inventory.SelectItemForValidation("Shears") || !mobs.SpawnEgg("Sheep", Vector3.Right))
                throw new InvalidOperationException("Sheep/shears setup failed.");
            Vector3 sheepAim = mobs.LastSpawnedMobAimPosition;
            Vector3 sheepRay = -sheepAim.Normalized();
            int pickupsBeforeShearing = survival.PickupCount;
            if (!mobs.TryShear(sheepAim - sheepRay, sheepRay)
                || survival.PickupCount != pickupsBeforeShearing + 1)
                throw new InvalidOperationException("Shearing did not create exactly one three-wool pickup.");
            if (!mobs.TryShear(sheepAim - sheepRay, sheepRay)
                || survival.PickupCount != pickupsBeforeShearing + 1)
                throw new InvalidOperationException("An already-shorn sheep duplicated wool.");
            mobs.Capture(world);
            if (world.Mobs.Find(mob => mob.Type == "Sheep") is not { Sheared: true, WoolRegrowSeconds: > 0f })
                throw new InvalidOperationException("Sheep wool state was not captured for saving.");

            inventory.AddItem("Wool", 3);
            inventory.AddItem("Wood", 3);
            foreach (int slot in new[] { 3, 4, 5 })
                if (!inventory.PlaceOneInCraftForValidation("Wool", slot))
                    throw new InvalidOperationException("Could not place wool in the bed recipe.");
            foreach (int slot in new[] { 6, 7, 8 })
                if (!inventory.PlaceOneInCraftForValidation("Wood", slot))
                    throw new InvalidOperationException("Could not place wood in the bed recipe.");
            if (inventory.CurrentRecipe() != "Bed" || !inventory.CraftOnceForValidation())
                throw new InvalidOperationException("The 3 wool + 3 wood bed recipe failed.");

            // Once placement has consumed the campfire, fill every slot. A
            // failed recovery must leave the world object intact and add none.
            if (!inventory.SelectItemForValidation("Campfire") || !survival.UseSelected(Vector3.Up))
                throw new InvalidOperationException("Second campfire placement failed.");
            for (int index = 0; index < 80; index++) inventory.AddItem("Filler " + index);
            int fullCampfireCount = survival.CampfireCount;
            survival.TryBreakPlacedObject(objectPosition + Vector3.Up * 3f, Vector3.Down);
            if (survival.CampfireCount != fullCampfireCount || inventory.CountItem("Campfire") != 0)
                throw new InvalidOperationException("Full inventory duplicated or destroyed the campfire.");

            world.GameMode = "Creative";
            int mobCountBefore = mobs.MobCount;
            if (!mobs.SpawnEgg("Chicken", Vector3.Right))
                throw new InvalidOperationException("Chicken spawn failed.");
            Vector3 mobAim = mobs.LastSpawnedMobAimPosition;
            Vector3 mobRayDirection = -mobAim.Normalized();
            if (!mobs.TryHit(mobAim - mobRayDirection, mobRayDirection)
                || mobs.MobCount != mobCountBefore || mobs.DyingSceneNodeCount != 1
                || survival.PickupCount < 1)
                throw new InvalidOperationException("Passive mob death did not drop, deregister and enter cleanup.");
            await ToSignal(GetTree().CreateTimer(0.75f), SceneTreeTimer.SignalName.Timeout);
            if (mobs.DyingSceneNodeCount != 0)
                throw new InvalidOperationException("Dead passive mob remained in the scene.");

            if (!monsters.SpawnEgg("Night Crawler Egg", Vector3.Right))
                throw new InvalidOperationException("Hostile spawn failed.");
            Vector3 monsterAim = monsters.FirstMonsterAimPosition;
            Vector3 monsterDirection = -monsterAim.Normalized();
            if (!monsters.TryHit(monsterAim - monsterDirection, monsterDirection) || monsters.MonsterCount != 0)
                throw new InvalidOperationException("Hostile mob was not deregistered on death.");
            await ToSignal(GetTree().CreateTimer(0.7f), SceneTreeTimer.SignalName.Timeout);
            if (monsters.MonsterSceneNodeCount != 0)
                throw new InvalidOperationException("Dead hostile mob remained in the scene.");

            main.QueueFree();
            for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            WorldStore.Delete(world);
            GameSession.Current = null;
            GD.Print("FOCUSED_GAMEPLAY_BEHAVIORS_TEST: PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null;
            GD.PushError("FOCUSED_GAMEPLAY_BEHAVIORS_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }
}
