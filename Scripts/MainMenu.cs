using Godot;
using System.Collections.Generic;

namespace HexaSphericalSandbox;

public partial class MainMenu : Control
{
    private ItemList _worldList = null!;
    private LineEdit _name = null!;
    private OptionButton _mode = null!;
    private OptionButton _quality = null!;
    private CheckButton _weather = null!;
    private CheckButton _interpolation = null!;
    private Label _details = null!;
    private readonly List<WorldData> _worlds = [];
    private bool _openingWorld;

    public override void _Ready()
    {
        DisplayServer.WindowSetTitle("HexaSpherical Sandbox — Alpha 0.0.3");
        _worldList = GetNode<ItemList>("Center/Panel/Layout/Worlds");
        _name = GetNode<LineEdit>("Center/Panel/Layout/NewName");
        _mode = GetNode<OptionButton>("Center/Panel/Layout/Mode");
        _quality = GetNode<OptionButton>("Center/Panel/Layout/Quality");
        _weather = GetNode<CheckButton>("Center/Panel/Layout/Weather");
        _interpolation = GetNode<CheckButton>("Center/Panel/Layout/Interpolation");
        _details = GetNode<Label>("Center/Panel/Layout/Details");
        _mode.AddItem("Creative"); _mode.AddItem("Survival");
        _quality.AddItem("Low — recommended for laptops");
        _quality.AddItem("Balanced");
        _quality.AddItem("High — desktop PC");
        _worldList.ItemSelected += index => ShowDetails((int)index);
        GetNode<Button>("Center/Panel/Layout/Create").Pressed += CreateWorld;
        GetNode<Button>("Center/Panel/Layout/Load").Pressed += LoadWorld;
        GetNode<Button>("Center/Panel/Layout/Delete").Pressed += DeleteWorld;
        Refresh();
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void Refresh()
    {
        _worlds.Clear(); _worlds.AddRange(WorldStore.List()); _worldList.Clear();
        foreach (var world in _worlds)
            _worldList.AddItem($"{world.Name} — {(world.GameMode == "Creative" ? "Creative" : "Survival")} — seed {world.Seed}");
        _details.Text = _worlds.Count == 0 ? "No worlds yet. Create your first world." : "Select a world.";
    }

    private void ShowDetails(int index)
    {
        var world = _worlds[index];
        _details.Text = $"Seed: {world.Seed}\nCreated: {world.CreatedUtc.ToLocalTime():g}\nLast played: {world.UpdatedUtc.ToLocalTime():g}";
    }

    private void CreateWorld()
    {
        if (_openingWorld) return;
        _openingWorld = true;
        GameSession.Current = WorldStore.Create(_name.Text, _mode.Selected == 0 ? "Creative" : "Survival");
        GameSession.Current.Quality = _quality.Selected switch { 1 => "Balanced", 2 => "High", _ => "Low" };
        GameSession.Current.WeatherEnabled = _weather.ButtonPressed;
        GameSession.Current.InterpolationEnabled = _interpolation.ButtonPressed;
        WorldStore.Save(GameSession.Current);
        GetTree().ChangeSceneToFile("res://Main.tscn");
    }

    private void LoadWorld()
    {
        if (_openingWorld) return;
        int[] selected = _worldList.GetSelectedItems();
        if (selected.Length == 0) return;
        _openingWorld = true;
        GameSession.Current = _worlds[selected[0]];
        GetTree().ChangeSceneToFile("res://Main.tscn");
    }

    private void DeleteWorld()
    {
        int[] selected = _worldList.GetSelectedItems();
        if (selected.Length == 0) return;
        WorldStore.Delete(_worlds[selected[0]]); Refresh();
    }
}
