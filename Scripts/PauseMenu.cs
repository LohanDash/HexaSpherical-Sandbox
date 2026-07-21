using Godot;

namespace HexaSphericalSandbox;

public partial class PauseMenu : CanvasLayer
{
    private Control _overlay = null!;
    private HSlider _renderDistance = null!;
    private Label _distanceValue = null!;
    private HexPlanet _planet = null!;
    private bool _open;
    private bool _initializing;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcessInput(true);
        _overlay = GetNode<Control>("Overlay");
        _renderDistance = GetNode<HSlider>("Overlay/Center/Panel/Layout/RenderDistance");
        _distanceValue = GetNode<Label>("Overlay/Center/Panel/Layout/DistanceValue");
        _planet = GetNode<HexPlanet>("../Planet");
        _initializing = true;
        _renderDistance.Value = _planet.StreamingDistance;
        _initializing = false;
        UpdateDistanceLabel();
        _renderDistance.ValueChanged += OnRenderDistanceChanged;
        GetNode<Button>("Overlay/Center/Panel/Layout/Resume").Pressed += Close;
        GetNode<Button>("Overlay/Center/Panel/Layout/SaveQuit").Pressed +=
            () => GetNode<Main>("..").SaveAndReturnToMenu();
        _overlay.Visible = false;
    }

    public override void _Input(InputEvent @event)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (GetNode<Control>("../InventoryUI/Inventory").Visible) return;
        if (@event is not InputEventKey { Pressed: true, Echo: false } key
            || key.Keycode != Key.Escape) return;
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    public bool IsOpen => _open;

    public void Toggle()
    {
        if (_open) Close(); else Open();
    }

    private void Open()
    {
        _open = true;
        _overlay.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = true;
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        _overlay.Visible = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (GameSession.Current != null) WorldStore.Save(GameSession.Current);
    }

    private void OnRenderDistanceChanged(double value)
    {
        if (_initializing) return;
        float distance = (float)value;
        _planet.SetRenderDistance(distance);
        if (GameSession.Current != null) GameSession.Current.RenderDistance = distance;
        UpdateDistanceLabel();
    }

    private void UpdateDistanceLabel() =>
        _distanceValue.Text = $"{Mathf.RoundToInt((float)_renderDistance.Value)} metres";
}
