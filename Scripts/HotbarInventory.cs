using Godot;
using System;
using System.Collections.Generic;

namespace HexaSphericalSandbox;

public partial class HotbarInventory : CanvasLayer
{
    public static readonly string[] Catalog = ["Grass Block", "Dirt Block", "Stone Block", "Sand Block", "Snow Block", "Purple Block",
        "Chicken Egg", "Cow Egg", "Night Crawler Egg", "Night Brute Egg", "Twig", "Stick", "Pebble", "Axe", "Primitive Pickaxe",
        "Stone Pickaxe", "Stone Axe", "Wood", "Campfire", "Raw Beef", "Cooked Beef", "Raw Chicken", "Cooked Chicken"];

    private const int InventorySize = 27;
    private HBoxContainer _hotbar = null!;
    private Control _inventory = null!;
    private GridContainer _catalogGrid = null!;
    private readonly ItemSlotButton[] _hotbarSlots = new ItemSlotButton[9];
    private readonly ItemSlotButton[] _inventorySlots = new ItemSlotButton[InventorySize];
    private readonly ItemSlotButton[] _craftSlots = new ItemSlotButton[9];
    private readonly Dictionary<string, Button> _creativeButtons = [];
    private Button _craftOutput = null!;
    private bool _open;
    private ItemSlotButton? _manualDragSource;
    private Label _manualDragPreview = null!;

    public bool IsOpen => _open;
    public bool HotbarAccessibleWhileOpen => _open && _hotbar.Visible
        && _hotbar.ZIndex > _inventory.ZIndex
        && Array.TrueForAll(_hotbarSlots, slot => slot != null && slot.MouseFilter == Control.MouseFilterEnum.Stop);
    public string SelectedItem => GameSession.Current is { } world && world.HotbarItems.Length == 9
        ? world.HotbarItems[Mathf.Clamp(world.SelectedHotbarSlot, 0, 8)] ?? "" : "";
    public int SelectedBlockType => Array.IndexOf(Catalog, SelectedItem) is int index && index is >= 0 and <= 5 ? index : -1;
    private WorldData World => GameSession.Current!;

    public override void _Ready()
    {
        SetProcessInput(true);
        _hotbar = GetNode<HBoxContainer>("Hotbar");
        _inventory = GetNode<Control>("Inventory");
        // Inventory/Dim covers the whole viewport. Keep the hotbar above that
        // overlay so its nine slots remain valid drag/drop targets while E is open.
        _inventory.ZIndex = 10;
        _hotbar.ZIndex = 20;
        _catalogGrid = GetNode<GridContainer>("Inventory/Center/Panel/Layout/Grid");
        NormalizeAndMigrate();
        BuildHotbar();
        BuildInventoryAndCrafting();
        _manualDragPreview = new Label
        {
            Name = "DragPreview",
            Visible = false,
            ZIndex = 100,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0.9f)
        };
        AddChild(_manualDragPreview);
        _inventory.Visible = false;
        Refresh();
    }

    public override void _Input(InputEvent input)
    {
        if (!_open && GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.E) { ToggleInventory(); GetViewport().SetInputAsHandled(); return; }
            if (!_open && key.Keycode >= Key.Key1 && key.Keycode <= Key.Key9) Select((int)(key.Keycode - Key.Key1));
        }
        if (_open && HandleManualDrag(input)) return;
        if (!_open && input is InputEventMouseButton { Pressed: true } wheel
            && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            Select(Mathf.PosMod(World.SelectedHotbarSlot + (wheel.ButtonIndex == MouseButton.WheelDown ? 1 : -1), 9));
            GetViewport().SetInputAsHandled();
        }
    }

    private bool HandleManualDrag(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                ItemSlotButton? source = SlotAt(button.Position);
                if (source == null || SlotCount(source.Area, source.SlotIndex) <= 0) return false;
                _manualDragSource = source;
                _manualDragPreview.Text = SlotLabel(source.Area, source.SlotIndex);
                _manualDragPreview.Position = button.Position + new Vector2(14f, 14f);
                _manualDragPreview.Visible = true;
                GetViewport().SetInputAsHandled();
                return true;
            }

            if (_manualDragSource == null) return false;
            ItemSlotButton sourceSlot = _manualDragSource;
            ItemSlotButton? target = SlotAt(button.Position);
            _manualDragSource = null;
            _manualDragPreview.Visible = false;
            if (target != null)
            {
                if (target == sourceSlot && sourceSlot.Area == ItemSlotButton.SlotArea.Hotbar)
                    Select(sourceSlot.SlotIndex);
                else
                    MoveOrMerge(sourceSlot.Area, sourceSlot.SlotIndex, target.Area, target.SlotIndex);
            }
            GetViewport().SetInputAsHandled();
            return true;
        }
        if (input is InputEventMouseMotion motion && _manualDragSource != null)
        {
            _manualDragPreview.Position = motion.Position + new Vector2(14f, 14f);
            GetViewport().SetInputAsHandled();
            return true;
        }
        return false;
    }

    private ItemSlotButton? SlotAt(Vector2 viewportPosition)
    {
        foreach (ItemSlotButton slot in _hotbarSlots)
            if (slot != null && slot.Visible && slot.GetGlobalRect().HasPoint(viewportPosition)) return slot;
        foreach (ItemSlotButton slot in _inventorySlots)
            if (slot != null && slot.Visible && slot.GetGlobalRect().HasPoint(viewportPosition)) return slot;
        foreach (ItemSlotButton slot in _craftSlots)
            if (slot != null && slot.Visible && slot.GetGlobalRect().HasPoint(viewportPosition)) return slot;
        return null;
    }

    private void BuildHotbar()
    {
        for (int slot = 0; slot < 9; slot++)
        {
            int selected = slot;
            _hotbarSlots[slot] = CreateSlot(ItemSlotButton.SlotArea.Hotbar, slot, new Vector2(78, 58));
            _hotbarSlots[slot].Pressed += () => Select(selected);
            _hotbar.AddChild(_hotbarSlots[slot]);
        }
    }

    private void BuildInventoryAndCrafting()
    {
        VBoxContainer layout = GetNode<VBoxContainer>("Inventory/Center/Panel/Layout");
        GetNode<Label>("Inventory/Center/Panel/Layout/Hint").Text = "Drag stacks. Right-click a stack to split half into the first empty slot.";
        _catalogGrid.Columns = 9;
        for (int slot = 0; slot < InventorySize; slot++)
        {
            _inventorySlots[slot] = CreateSlot(ItemSlotButton.SlotArea.Inventory, slot, new Vector2(66, 48));
            _catalogGrid.AddChild(_inventorySlots[slot]);
        }

        var craftTitle = new Label { Text = "3 × 3 CRAFTING", HorizontalAlignment = HorizontalAlignment.Center };
        layout.AddChild(craftTitle);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var craftGrid = new GridContainer { Columns = 3 };
        for (int slot = 0; slot < 9; slot++)
        {
            _craftSlots[slot] = CreateSlot(ItemSlotButton.SlotArea.Craft, slot, new Vector2(66, 48));
            craftGrid.AddChild(_craftSlots[slot]);
        }
        row.AddChild(craftGrid);
        _craftOutput = new Button { CustomMinimumSize = new Vector2(150, 64), Text = "No recipe" };
        _craftOutput.Pressed += Craft;
        row.AddChild(_craftOutput);
        layout.AddChild(row);

        if (GameSession.IsCreative)
        {
            var paletteTitle = new Label { Text = "CREATIVE CATALOG", HorizontalAlignment = HorizontalAlignment.Center };
            layout.AddChild(paletteTitle);
            var palette = new GridContainer { Columns = 5 };
            foreach (string item in Catalog)
            {
                var button = new Button { Text = item, CustomMinimumSize = new Vector2(116, 36) };
                button.Pressed += () => AssignCreative(item);
                _creativeButtons[item] = button;
                palette.AddChild(button);
            }
            layout.AddChild(palette);
        }
    }

    private ItemSlotButton CreateSlot(ItemSlotButton.SlotArea area, int slot, Vector2 size) => new()
    {
        OwnerInventory = this,
        Area = area,
        SlotIndex = slot,
        CustomMinimumSize = size,
        MouseFilter = Control.MouseFilterEnum.Stop,
        MouseDefaultCursorShape = Control.CursorShape.PointingHand
    };

    public int CountItem(string item)
    {
        int total = 0;
        foreach (ItemSlotButton.SlotArea area in new[] { ItemSlotButton.SlotArea.Hotbar, ItemSlotButton.SlotArea.Inventory })
        for (int slot = 0; slot < AreaLength(area); slot++)
            if (SlotItem(area, slot) == item) total += SlotCount(area, slot);
        return total;
    }

    public void AddItem(string item, int amount = 1)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(item)) return;
        bool stackable = !IsTool(item);
        if (stackable)
        {
            foreach (ItemSlotButton.SlotArea area in new[] { ItemSlotButton.SlotArea.Hotbar, ItemSlotButton.SlotArea.Inventory })
            for (int slot = 0; slot < AreaLength(area); slot++)
                if (SlotItem(area, slot) == item) { SetSlot(area, slot, item, SlotCount(area, slot) + amount); Refresh(); return; }
        }
        foreach (ItemSlotButton.SlotArea area in new[] { ItemSlotButton.SlotArea.Hotbar, ItemSlotButton.SlotArea.Inventory })
        for (int slot = 0; slot < AreaLength(area); slot++)
        {
            if (SlotCount(area, slot) > 0) continue;
            SetSlot(area, slot, item, amount);
            EnsureToolDurability(item);
            Refresh();
            return;
        }
    }

    public bool RemoveItem(string item, int amount = 1)
    {
        if (GameSession.IsCreative) return true;
        if (CountItem(item) < amount) return false;
        foreach (ItemSlotButton.SlotArea area in new[] { ItemSlotButton.SlotArea.Hotbar, ItemSlotButton.SlotArea.Inventory })
        for (int slot = 0; slot < AreaLength(area) && amount > 0; slot++)
        {
            if (SlotItem(area, slot) != item) continue;
            int take = Math.Min(amount, SlotCount(area, slot));
            SetSlot(area, slot, item, SlotCount(area, slot) - take);
            amount -= take;
        }
        Refresh();
        return true;
    }

    public bool ConsumeSelected()
    {
        if (GameSession.IsCreative) return true;
        int slot = World.SelectedHotbarSlot;
        int count = SlotCount(ItemSlotButton.SlotArea.Hotbar, slot);
        if (count <= 0) return false;
        SetSlot(ItemSlotButton.SlotArea.Hotbar, slot, SelectedItem, count - 1);
        Refresh();
        return true;
    }

    public bool ConsumeToolUse(string tool)
    {
        if (GameSession.IsCreative) return true;
        if (SelectedItem != tool) return false;
        EnsureToolDurability(tool);
        int remaining = --World.ToolDurability[tool];
        if (remaining > 0) { Refresh(); return true; }
        SetSlot(ItemSlotButton.SlotArea.Hotbar, World.SelectedHotbarSlot, "", 0);
        World.ToolDurability.Remove(tool);
        if (tool == "Primitive Pickaxe") { AddItem("Stick"); AddItem("Pebble"); }
        Refresh();
        return true;
    }

    public int ToolUsesRemaining(string tool)
    {
        EnsureToolDurability(tool);
        return World.ToolDurability.GetValueOrDefault(tool);
    }

    public void AddBlock(int type) => AddItem(Catalog[Mathf.Clamp(type, 0, 5)]);
    public void AddSelectedItem() => AddItem(SelectedItem);

    public void DropSomeItems(Action<string, int> drop)
    {
        if (GameSession.IsCreative) return;
        foreach (ItemSlotButton.SlotArea area in new[] { ItemSlotButton.SlotArea.Hotbar, ItemSlotButton.SlotArea.Inventory })
        for (int slot = 0; slot < AreaLength(area); slot++)
        {
            int amount = SlotCount(area, slot) / 3;
            if (amount <= 0) continue;
            string item = SlotItem(area, slot);
            SetSlot(area, slot, item, SlotCount(area, slot) - amount);
            drop(item, amount);
        }
        Refresh();
    }

    public bool CanDrop(Variant data)
        => data.VariantType == Variant.Type.Dictionary;

    public void DropStack(Variant data, ItemSlotButton.SlotArea targetArea, int targetSlot)
    {
        var dictionary = data.AsGodotDictionary();
        if (!dictionary.ContainsKey("area") || !dictionary.ContainsKey("slot")) return;
        var sourceArea = (ItemSlotButton.SlotArea)(int)dictionary["area"];
        int sourceSlot = (int)dictionary["slot"];
        MoveOrMerge(sourceArea, sourceSlot, targetArea, targetSlot);
    }

    public void SplitStack(ItemSlotButton.SlotArea area, int slot)
    {
        int count = SlotCount(area, slot);
        if (count < 2) return;
        string item = SlotItem(area, slot);
        int target = FirstEmptyInventorySlot();
        if (target < 0) return;
        int moved = count / 2;
        SetSlot(area, slot, item, count - moved);
        SetSlot(ItemSlotButton.SlotArea.Inventory, target, item, moved);
        Refresh();
    }

    private void MoveOrMerge(ItemSlotButton.SlotArea sourceArea, int sourceSlot, ItemSlotButton.SlotArea targetArea, int targetSlot)
    {
        if (sourceArea == targetArea && sourceSlot == targetSlot) return;
        string sourceItem = SlotItem(sourceArea, sourceSlot);
        int sourceCount = SlotCount(sourceArea, sourceSlot);
        if (sourceCount <= 0) return;
        string targetItem = SlotItem(targetArea, targetSlot);
        int targetCount = SlotCount(targetArea, targetSlot);
        if (targetCount <= 0 || targetItem == sourceItem && !IsTool(sourceItem))
        {
            SetSlot(targetArea, targetSlot, sourceItem, targetCount + sourceCount);
            SetSlot(sourceArea, sourceSlot, "", 0);
        }
        else
        {
            SetSlot(targetArea, targetSlot, sourceItem, sourceCount);
            SetSlot(sourceArea, sourceSlot, targetItem, targetCount);
        }
        Refresh();
    }

    private void Craft()
    {
        string? output = Recipe();
        if (output == null) return;
        for (int slot = 0; slot < 9; slot++)
            if (World.CraftSlotCounts[slot] > 0) SetSlot(ItemSlotButton.SlotArea.Craft, slot, World.CraftSlotItems[slot], World.CraftSlotCounts[slot] - 1);
        AddItem(output);
        SoundManager.Play(SoundKind.Craft);
        Refresh();
    }

    public string? CurrentRecipe() => Recipe();

    public bool MoveStackForValidation(ItemSlotButton.SlotArea sourceArea, int sourceSlot,
        ItemSlotButton.SlotArea targetArea, int targetSlot)
    {
        int before = SlotCount(sourceArea, sourceSlot);
        MoveOrMerge(sourceArea, sourceSlot, targetArea, targetSlot);
        return before > 0 && SlotCount(sourceArea, sourceSlot) == 0;
    }

    public bool PlaceOneInCraftForValidation(string item, int slot)
    {
        if (slot < 0 || slot >= 9 || SlotCount(ItemSlotButton.SlotArea.Craft, slot) > 0 || !RemoveItem(item)) return false;
        SetSlot(ItemSlotButton.SlotArea.Craft, slot, item, 1);
        Refresh();
        return true;
    }

    public bool CraftOnceForValidation()
    {
        string? before = Recipe();
        if (before == null) return false;
        Craft();
        return CountItem(before) > 0;
    }

    public bool SelectItemForValidation(string item)
    {
        for (int slot = 0; slot < 9; slot++)
        {
            if (SlotItem(ItemSlotButton.SlotArea.Hotbar, slot) != item) continue;
            Select(slot);
            return true;
        }
        return false;
    }

    public int FindItemSlotForValidation(ItemSlotButton.SlotArea area, string item)
    {
        for (int slot = 0; slot < AreaLength(area); slot++) if (SlotItem(area, slot) == item) return slot;
        return -1;
    }

    public Vector2 SlotCenterForValidation(ItemSlotButton.SlotArea area, int slot)
    {
        ItemSlotButton button = area switch
        {
            ItemSlotButton.SlotArea.Hotbar => _hotbarSlots[slot],
            ItemSlotButton.SlotArea.Inventory => _inventorySlots[slot],
            _ => _craftSlots[slot]
        };
        return button.GetGlobalRect().GetCenter();
    }

    private string? Recipe()
    {
        bool Match(string?[] pattern)
        {
            for (int slot = 0; slot < 9; slot++)
            {
                string actual = World.CraftSlotCounts[slot] > 0 ? World.CraftSlotItems[slot] : "";
                if (actual != (pattern[slot] ?? "")) return false;
            }
            return true;
        }
        if (Match([null,"Twig",null, null,"Twig",null, null,"Twig",null])) return "Stick";
        if (Match([null,"Pebble",null, null,"Stick",null, null,"Stick",null])) return "Axe";
        if (Match(["Pebble","Pebble","Pebble", null,"Stick",null, null,"Stick",null])) return "Primitive Pickaxe";
        if (Match(["Stone Block","Stone Block","Stone Block", null,"Stick",null, null,"Stick",null])) return "Stone Pickaxe";
        if (Match(["Stone Block","Stone Block",null, "Stone Block","Stick",null, null,"Stick",null])) return "Stone Axe";
        string?[] campfire = ["Wood","Wood","Wood", "Wood",null,"Wood", "Wood","Wood","Wood"];
        return Match(campfire) ? "Campfire" : null;
    }

    public void Refresh()
    {
        for (int slot = 0; slot < 9; slot++) RefreshSlot(_hotbarSlots[slot], slot == World.SelectedHotbarSlot);
        for (int slot = 0; slot < InventorySize; slot++) RefreshSlot(_inventorySlots[slot], false);
        for (int slot = 0; slot < 9; slot++) RefreshSlot(_craftSlots[slot], false);
        if (_craftOutput != null)
        {
            string? output = Recipe();
            _craftOutput.Text = output == null ? "No recipe" : $"CRAFT → {output}";
            _craftOutput.Disabled = output == null;
        }
    }

    private void RefreshSlot(ItemSlotButton? button, bool selected)
    {
        if (button == null) return;
        string item = SlotItem(button.Area, button.SlotIndex);
        int count = SlotCount(button.Area, button.SlotIndex);
        button.Text = count <= 0 ? "" : $"{Short(item)}\n×{count}";
        if (count > 0 && IsTool(item)) button.Text += $"  [{World.ToolDurability.GetValueOrDefault(item, MaxDurability(item))}]";
        button.TooltipText = count <= 0 ? "Empty" : item;
        Color colour = count <= 0 ? new Color(0.22f,0.24f,0.28f) : ItemColor(item);
        button.Modulate = selected ? colour.Lightened(0.35f) : colour.Darkened(0.12f);
    }

    public int SlotCount(ItemSlotButton.SlotArea area, int slot) => area switch
    {
        ItemSlotButton.SlotArea.Hotbar => World.HotbarCounts[slot],
        ItemSlotButton.SlotArea.Inventory => World.InventorySlotCounts[slot],
        _ => World.CraftSlotCounts[slot]
    };

    public string SlotLabel(ItemSlotButton.SlotArea area, int slot)
        => $"{SlotItem(area, slot)} ×{SlotCount(area, slot)}";

    private string SlotItem(ItemSlotButton.SlotArea area, int slot) => area switch
    {
        ItemSlotButton.SlotArea.Hotbar => World.HotbarItems[slot] ?? "",
        ItemSlotButton.SlotArea.Inventory => World.InventorySlotItems[slot] ?? "",
        _ => World.CraftSlotItems[slot] ?? ""
    };

    private void SetSlot(ItemSlotButton.SlotArea area, int slot, string item, int count)
    {
        if (count <= 0) { item = ""; count = 0; }
        switch (area)
        {
            case ItemSlotButton.SlotArea.Hotbar: World.HotbarItems[slot] = item; World.HotbarCounts[slot] = count; break;
            case ItemSlotButton.SlotArea.Inventory: World.InventorySlotItems[slot] = item; World.InventorySlotCounts[slot] = count; break;
            default: World.CraftSlotItems[slot] = item; World.CraftSlotCounts[slot] = count; break;
        }
    }

    private int AreaLength(ItemSlotButton.SlotArea area) => area == ItemSlotButton.SlotArea.Inventory ? InventorySize : 9;
    private int FirstEmptyInventorySlot()
    {
        for (int slot = 0; slot < InventorySize; slot++) if (World.InventorySlotCounts[slot] <= 0) return slot;
        return -1;
    }

    private void NormalizeAndMigrate()
    {
        World.HotbarItems = Resize(World.HotbarItems, 9);
        World.HotbarCounts = Resize(World.HotbarCounts, 9);
        World.InventorySlotItems = Resize(World.InventorySlotItems, InventorySize);
        World.InventorySlotCounts = Resize(World.InventorySlotCounts, InventorySize);
        World.CraftSlotItems = Resize(World.CraftSlotItems, 9);
        World.CraftSlotCounts = Resize(World.CraftSlotCounts, 9);
        World.SelectedHotbarSlot = Mathf.Clamp(World.SelectedHotbarSlot, 0, 8);
        foreach ((string item, int count) in new Dictionary<string,int>(World.InventoryItems))
            if (count > 0) AddItem(item, count);
        World.InventoryItems.Clear();
        for (int slot = 0; slot < 9; slot++) if (World.HotbarCounts[slot] <= 0) World.HotbarItems[slot] = "";
    }

    private void Select(int slot) { World.SelectedHotbarSlot = Mathf.Clamp(slot, 0, 8); Refresh(); }
    private void AssignCreative(string item)
    {
        SetSlot(ItemSlotButton.SlotArea.Hotbar, World.SelectedHotbarSlot, item, 1);
        EnsureToolDurability(item);
        Refresh();
    }
    private void ToggleInventory()
    {
        _open = !_open;
        _inventory.Visible = _open;
        _hotbar.Visible = true;
        if (!_open)
        {
            _manualDragSource = null;
            if (_manualDragPreview != null) _manualDragPreview.Visible = false;
        }
        Input.MouseMode = _open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }
    private void EnsureToolDurability(string item)
    {
        if (IsTool(item) && !World.ToolDurability.ContainsKey(item)) World.ToolDurability[item] = MaxDurability(item);
    }
    private static bool IsTool(string item) => item is "Axe" or "Primitive Pickaxe" or "Stone Pickaxe" or "Stone Axe";
    private static T[] Resize<T>(T[]? source, int length)
    {
        T[] result = new T[length];
        if (source != null) Array.Copy(source, result, Math.Min(source.Length, length));
        return result;
    }
    private static int MaxDurability(string item) => item switch { "Primitive Pickaxe" => 4, "Stone Pickaxe" or "Stone Axe" => 128, "Axe" => 12, _ => 1 };
    private static string Short(string item) => item.Replace(" Block", "").Replace("Primitive ", "P.").Replace("Stone ", "S.").Replace("Night ", "N.").Replace(" Egg", "Egg");
    private static Color ItemColor(string item)
    {
        int block = Array.IndexOf(Catalog, item);
        if (block is >= 0 and <= 5) return BlockCatalog.Get(block).Color;
        return item switch
        {
            "Axe" or "Primitive Pickaxe" => new Color(0.58f, 0.56f, 0.48f),
            "Stone Pickaxe" or "Stone Axe" => new Color(0.48f, 0.52f, 0.56f),
            "Pebble" => new Color(0.5f, 0.52f, 0.55f),
            "Raw Beef" or "Raw Chicken" => new Color(0.72f, 0.18f, 0.16f),
            "Cooked Beef" or "Cooked Chicken" => new Color(0.55f, 0.25f, 0.08f),
            "Wood" or "Twig" or "Stick" or "Campfire" => new Color(0.48f, 0.28f, 0.1f),
            _ => new Color(0.65f, 0.35f, 0.12f)
        };
    }
}
