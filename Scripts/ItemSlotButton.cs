using Godot;

namespace HexaSphericalSandbox;

public partial class ItemSlotButton : Button
{
    public enum SlotArea { Hotbar, Inventory, Craft }
    public HotbarInventory OwnerInventory { get; set; } = null!;
    public SlotArea Area { get; set; }
    public int SlotIndex { get; set; }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (OwnerInventory.SlotCount(Area, SlotIndex) <= 0) return default;
        var preview = new Label
        {
            Text = OwnerInventory.SlotLabel(Area, SlotIndex),
            Modulate = new Color(1f, 1f, 1f, 0.9f)
        };
        SetDragPreview(preview);
        return new Godot.Collections.Dictionary
        {
            ["area"] = (int)Area,
            ["slot"] = SlotIndex
        };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
        => OwnerInventory.CanDrop(data);

    public override void _DropData(Vector2 atPosition, Variant data)
        => OwnerInventory.DropStack(data, Area, SlotIndex);

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            OwnerInventory.SplitStack(Area, SlotIndex);
            AcceptEvent();
        }
    }
}
