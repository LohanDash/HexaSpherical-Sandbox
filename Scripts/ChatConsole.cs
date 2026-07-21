using Godot;

namespace HexaSphericalSandbox;

public partial class ChatConsole : CanvasLayer
{
    private LineEdit _input = null!;
    private Label _history = null!;
    private bool _open;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcessInput(true);
        _input = GetNode<LineEdit>("Input");
        _history = GetNode<Label>("History");
        _input.Visible = false;
        _input.TextSubmitted += Submit;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (!_open && (key.Keycode == Key.T || key.Keycode == Key.Slash))
        {
            _open = true; _input.Visible = true; _input.Text = key.Keycode == Key.Slash ? "/" : "";
            _input.GrabFocus(); Input.MouseMode = Input.MouseModeEnum.Visible;
            GetViewport().SetInputAsHandled();
        }
        else if (_open && key.Keycode == Key.Escape) Close();
    }

    private void Submit(string text)
    {
        string result = Execute(text.Trim());
        _history.Text = result;
        _history.Visible = true;
        GetTree().CreateTimer(5).Timeout += () => _history.Visible = false;
        Close();
    }

    private string Execute(string command)
    {
        string normalized = command.ToLowerInvariant();
        string? mode = normalized switch
        {
            "/gamemode creative" or "/gamemode 1" => "Creative",
            "/gamemode survival" or "/gamemode 0" => "Survival",
            _ => null
        };
        if (mode == null) return $"Unknown command: {command}";
        if (GameSession.Current != null) GameSession.Current.GameMode = mode;
        GetNode<SphericalPlayer>("../Player").OnGameModeChanged();
        return $"Game mode set to {mode}.";
    }

    private void Close()
    {
        _open = false; _input.Visible = false; _input.ReleaseFocus();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        GetViewport().SetInputAsHandled();
    }
}
