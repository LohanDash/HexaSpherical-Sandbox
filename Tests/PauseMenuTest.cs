using Godot;
using System;
using System.Linq;

namespace HexaSphericalSandbox.Tests;

public partial class PauseMenuTest : Node
{
    public override async void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData
            {
                Id = "__pause_menu_test__" + Guid.NewGuid().ToString("N"),
                Seed = 73421,
                GenerationPreset = "PreIndev",
                Quality = "Low"
            };
            Node main = GD.Load<PackedScene>("res://Main.tscn").Instantiate();
            AddChild(main);
            for (int frame = 0; frame < 24; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            HexPlanet planet = main.GetNode<HexPlanet>("Planet");
            if (planet.PhysicsChunkCount < 1 || !planet.HasPhysicsNear(Vector3.Up))
                throw new InvalidOperationException("The streamed terrain did not create a physical collision mesh.");

            SphericalPlayer player = main.GetNode<SphericalPlayer>("Player");
            for (int frame = 0; frame < 180; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            Vector3 playerDirection = player.GlobalPosition.Normalized();
            float playerFeet = player.GlobalPosition.Length() - player.GroundClearance;
            float supportingFloor = planet.FloorRadius(playerDirection, playerFeet + player.MaxStepHeight);
            if (playerFeet < supportingFloor - 0.06f)
                throw new InvalidOperationException($"Player fell through terrain: feet={playerFeet}, floor={supportingFloor}.");

            MobManager mobs = main.GetNode<MobManager>("MobManager");
            NightMonsterManager monsters = main.GetNode<NightMonsterManager>("NightMonsterManager");
            HotbarInventory inventory = main.GetNode<HotbarInventory>("InventoryUI");
            StarlingFlock flock = main.GetNode<StarlingFlock>("StarlingFlock");
            NatureSystem nature = main.GetNode<NatureSystem>("NatureSystem");
            if (GameSession.Current.HotbarCounts.Sum() != 0 || GameSession.Current.InventorySlotCounts.Sum() != 0)
                throw new InvalidOperationException("A fresh inventory is not empty.");
            int passiveBefore = mobs.MobCount;
            if (!mobs.SpawnEgg("Chicken", Vector3.Up) || mobs.MobCount != passiveBefore + 1)
                throw new InvalidOperationException("Passive spawn egg failed.");
            if (!mobs.SpawnEgg("Cow", Vector3.Up))
                throw new InvalidOperationException("Cow spawn failed.");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (!mobs.AllMobLocomotionReady)
                throw new InvalidOperationException("Cow/chicken locomotion animation is not active.");
            if (!monsters.SpawnEgg("Night Crawler Egg", Vector3.Up))
                throw new InvalidOperationException("Hostile spawn egg failed.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (monsters.EggMonsterCount != 1)
                throw new InvalidOperationException("Egg-spawned monster disappeared in Creative/daylight.");
            Vector3 monsterAim = monsters.FirstMonsterAimPosition;
            Vector3 monsterRayDirection = -monsterAim.Normalized();
            Vector3 monsterRayOrigin = monsterAim - monsterRayDirection;
            if (!monsters.TryHit(monsterRayOrigin, monsterRayDirection)
                || monsters.EggMonsterCount != 0)
                throw new InvalidOperationException("Hostile monster targeting or death failed.");

            if (!flock.ValidateNestConstruction())
                throw new InvalidOperationException("Two starlings failed to build and persist a nest.");
            float twigRatio = nature.TwigEligibleRatioForValidation();
            if (twigRatio < 0.75f || twigRatio > 0.85f || !nature.ValidateTwigRegrowth())
                throw new InvalidOperationException($"Twig availability/regrowth is invalid: {twigRatio:P0} eligible.");
            int birdsBeforeHawk = flock.BirdCount;
            if (!flock.ValidateHawkCatch() || flock.BirdCount != birdsBeforeHawk - 1)
                throw new InvalidOperationException("The hawk failed to kill a starling.");

            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            PauseMenu menu = main.GetNode<PauseMenu>("PauseMenu");
            if (!GetTree().Paused || !menu.IsOpen)
                throw new InvalidOperationException("Escape did not open the pause menu.");

            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (GetTree().Paused || menu.IsOpen)
                throw new InvalidOperationException("Escape did not close the pause menu.");

            Input.ParseInputEvent(new InputEventKey { Keycode = Key.E, Pressed = true });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!inventory.IsOpen || !inventory.HotbarAccessibleWhileOpen)
                throw new InvalidOperationException("The hotbar is hidden or blocked while the inventory is open.");
            inventory.AddItem("Grass Block");
            int dragSourceSlot = inventory.FindItemSlotForValidation(ItemSlotButton.SlotArea.Hotbar, "Grass Block");
            Vector2 dragSource = inventory.SlotCenterForValidation(ItemSlotButton.SlotArea.Hotbar, dragSourceSlot);
            Vector2 dragTarget = inventory.SlotCenterForValidation(ItemSlotButton.SlotArea.Inventory, 5);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = dragSource });
            Input.ParseInputEvent(new InputEventMouseMotion { Position = dragTarget, Relative = dragTarget - dragSource });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = dragTarget });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (inventory.SlotCount(ItemSlotButton.SlotArea.Hotbar, dragSourceSlot) != 0
                || inventory.SlotCount(ItemSlotButton.SlotArea.Inventory, 5) != 1)
                throw new InvalidOperationException("Real mouse drag/drop did not move the stack.");
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.E, Pressed = true });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (inventory.IsOpen) throw new InvalidOperationException("E did not close the inventory.");

            GameSession.Current.GameMode = "Survival";
            player.OnGameModeChanged();

            inventory.AddItem("Twig", 3);
            int twigSlot = inventory.FindItemSlotForValidation(ItemSlotButton.SlotArea.Hotbar, "Twig");
            if (twigSlot < 0 || !inventory.MoveStackForValidation(ItemSlotButton.SlotArea.Hotbar, twigSlot,
                ItemSlotButton.SlotArea.Inventory, 0) || inventory.CountItem("Twig") != 3)
                throw new InvalidOperationException("Inventory drag/drop lost a stack.");

            inventory.AddItem("Pebble", 3);
            inventory.AddItem("Stick", 2);
            foreach (int slot in new[] { 0, 1, 2 })
                if (!inventory.PlaceOneInCraftForValidation("Pebble", slot)) throw new InvalidOperationException("Could not place pebbles in crafting grid.");
            foreach (int slot in new[] { 4, 7 })
                if (!inventory.PlaceOneInCraftForValidation("Stick", slot)) throw new InvalidOperationException("Could not place sticks in crafting grid.");
            if (inventory.CurrentRecipe() != "Primitive Pickaxe" || !inventory.CraftOnceForValidation()
                || !inventory.SelectItemForValidation("Primitive Pickaxe"))
                throw new InvalidOperationException("Primitive Pickaxe recipe failed.");
            for (int use = 0; use < 4; use++) inventory.ConsumeToolUse("Primitive Pickaxe");
            if (inventory.CountItem("Primitive Pickaxe") != 0 || inventory.CountItem("Stick") < 1 || inventory.CountItem("Pebble") < 1)
                throw new InvalidOperationException("Primitive Pickaxe did not break after four blocks and refund materials.");

            int acceptedHostiles = 0;
            for (int spawn = 0; spawn < 5; spawn++) if (monsters.SpawnEgg("Night Crawler Egg", Vector3.Up)) acceptedHostiles++;
            if (acceptedHostiles != 3 || monsters.MaximumCountInAnyChunk() > 3)
                throw new InvalidOperationException("Hostile per-chunk cap is not exactly three.");

            int footstepsBefore = player.FootstepSoundCount;
            for (int sample = 0; sample < 40; sample++) player.RegisterFootstepTravelForValidation(0.12f, true, false);
            int footsteps = player.FootstepSoundCount - footstepsBefore;
            if (footsteps != 3)
                throw new InvalidOperationException($"Walking cadence is invalid: {footsteps} sounds across 4.8 metres.");
            footstepsBefore = player.FootstepSoundCount;
            for (int sample = 0; sample < 40; sample++) player.RegisterFootstepTravelForValidation(0.12f, true, true);
            int sprintFootsteps = player.FootstepSoundCount - footstepsBefore;
            if (sprintFootsteps <= footsteps || sprintFootsteps > 5)
                throw new InvalidOperationException($"Sprint cadence is invalid: walk={footsteps}, sprint={sprintFootsteps}.");

            player.ApplyDamage(200f);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Control deathMenu = main.GetNode<Control>("HUD/DeathMenu");
            if (!player.IsDead || !deathMenu.Visible || Input.MouseMode != Input.MouseModeEnum.Visible)
                throw new InvalidOperationException("Death did not lock gameplay and open its menu.");
            Button respawn = deathMenu.FindChild("Respawn", true, false) as Button
                ?? throw new InvalidOperationException("Respawn button is missing.");
            respawn.EmitSignal(Button.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // Headless Godot deliberately keeps the OS cursor visible, so the
            // integration test asserts gameplay/menu state here. Runtime code
            // captures the cursor immediately and again on the following frame.
            if (player.IsDead || deathMenu.Visible)
                throw new InvalidOperationException($"Respawn did not restore gameplay cleanly: dead={player.IsDead}, menu={deathMenu.Visible}.");

            main.QueueFree();
            for (int frame = 0; frame < 4; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (GameSession.Current is { } testWorld) WorldStore.Delete(testWorld);
            GameSession.Current = null;
            GD.Print("PAUSE_MENU_TEST: PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GetTree().Paused = false;
            GameSession.Current = null;
            GD.PushError("PAUSE_MENU_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }
}
