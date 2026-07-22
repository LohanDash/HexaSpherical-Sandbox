using Godot;

namespace HexaSphericalSandbox;

public partial class SphericalPlayer : CharacterBody3D
{
    [Export] public float MoveSpeed { get; set; } = 7.0f;
    [Export] public float Acceleration { get; set; } = 24.0f;
    [Export] public float Gravity { get; set; } = 22.0f;
    [Export] public float JumpSpeed { get; set; } = 11.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public float GroundClearance { get; set; } = 0.78f;
    [Export] public float MaxStepHeight { get; set; } = 0.35f;
    [Export] public float BodyHeight { get; set; } = 1.5f;
    [Export] public float BodyRadius { get; set; } = 0.21f;
    public float Health { get; private set; } = 100f;
    public bool IsDead => _dead;

    private Node3D _pivot = null!;
    private Camera3D _camera = null!;
    private MeshInstance3D _bodyMesh = null!;
    private SpotLight3D _flashlight = null!;
    private MeshInstance3D _flashlightModel = null!;
    private Node3D _flashlightRig = null!;
    private HexPlanet _planet = null!;
    private float _pitch = -0.22f;
    private bool _surfaceGrounded;
    private int _viewMode;
    private bool _flying;
    private double _lastJumpTapAt = -10.0;
    private float _flashlightProbeCooldown;
    private Label _flightLabel = null!;
    private Label _healthLabel = null!;
    private MusicManager _music = null!;
    private HotbarInventory _inventory = null!;
    private Label _deathLabel = null!;
    private bool _dead;
    private Control _deathMenu = null!;
    private float _airbornePeakRadius;
    private SurvivalSystem _survival = null!;
    private NatureSystem _nature = null!;
    private MobManager _mobs = null!;
    private NightMonsterManager _monsters = null!;
    private bool _miningHeld;
    private float _miningProgress;
    private Vector3 _miningDirection;
    private Label _miningLabel = null!;
    private Vector3 _lastFootstepPosition;
    private float _footstepDistance;
    private Node3D _hands = null!;
    private Node3D _leftShoulder = null!;
    private Node3D _rightShoulder = null!;
    private MeshInstance3D _heldItem = null!;
    private string _renderedHeldItem = "";
    private float _handSwing;
    public int FootstepSoundCount { get; private set; }
    public bool FlashlightUsesRightHandPivot => _flashlightRig.GetParent() == _rightShoulder;
    public bool HeldItemUsesLeftHandPivot => _heldItem.GetParent() == _leftShoulder;

    public override void _Ready()
    {
        _pivot = GetNode<Node3D>("Pivot");
        _camera = GetNode<Camera3D>("Pivot/Camera3D");
        _bodyMesh = GetNode<MeshInstance3D>("BodyMesh");
        _flashlight = GetNode<SpotLight3D>("Pivot/FlashlightRig/Flashlight");
        _flashlightModel = GetNode<MeshInstance3D>("Pivot/FlashlightRig/FlashlightModel");
        _flashlightRig = GetNode<Node3D>("Pivot/FlashlightRig");
        _flightLabel = GetNode<Label>("../HUD/FlightLabel");
        _healthLabel = GetNode<Label>("../HUD/HealthLabel");
        _inventory = GetNode<HotbarInventory>("../InventoryUI");
        _deathLabel = GetNode<Label>("../HUD/DeathLabel");
        _music = GetNode<MusicManager>("../MusicManager");
        _planet = GetNode<HexPlanet>("../Planet");
        _survival = GetNode<SurvivalSystem>("../SurvivalSystem");
        _nature = GetNode<NatureSystem>("../NatureSystem");
        _mobs = GetNode<MobManager>("../MobManager");
        _monsters = GetNode<NightMonsterManager>("../NightMonsterManager");
        _miningLabel = new Label { Position = new Vector2(520, 410), Size = new Vector2(300, 34), HorizontalAlignment = HorizontalAlignment.Center };
        _miningLabel.AddThemeFontSizeOverride("font_size", 18);
        GetNode<CanvasLayer>("../HUD").AddChild(_miningLabel);
        Health = Mathf.Clamp(GameSession.Current?.Health ?? 100f, 1f, 100f);
        WorldData? world = GameSession.Current;
        float[] savedPosition = world?.PlayerPosition ?? [];
        bool positionIsCurrent = world?.SaveVersion >= 5 && savedPosition.Length == 3;
        Vector3 saved = positionIsCurrent
            ? new Vector3(savedPosition[0], savedPosition[1], savedPosition[2]) : Vector3.Zero;
        Vector3 savedDirection = saved.LengthSquared() > 1f ? saved.Normalized() : Vector3.Up;
        float savedFeetRadius = saved.Length() - GroundClearance;
        float savedFloorRadius = _planet.FloorRadius(savedDirection, savedFeetRadius + MaxStepHeight);
        bool savedPositionHasRoom = positionIsCurrent && saved.LengthSquared() > 1f
            && savedFeetRadius >= savedFloorRadius - 0.06f
            && _planet.HasRoom(savedDirection, savedFeetRadius, BodyHeight);
        GlobalPosition = savedPositionHasRoom
            ? saved
            : savedDirection * (_planet.SurfaceRadius(savedDirection) + GroundClearance + 1.2f);
        if (world != null && (world.SaveVersion < 5 || !savedPositionHasRoom))
        {
            world.SaveVersion = 5;
            world.PlayerPosition = [GlobalPosition.X, GlobalPosition.Y, GlobalPosition.Z];
        }
        Input.MouseMode = Input.MouseModeEnum.Captured;
        FloorSnapLength = 0.22f;
        SafeMargin = 0.045f;
        MaxSlides = 8;
        AlignToPlanet(Vector3.Up);
        UpdateHealthLabel();
        BuildDeathMenu();
        _airbornePeakRadius = GlobalPosition.Length();
        _lastFootstepPosition = GlobalPosition;
        BuildFirstPersonHands();
    }

    public override void _Process(double deltaValue)
    {
        float delta = (float)deltaValue;
        UpdateFirstPersonHands(delta);
        if (_miningHeld && !GameSession.IsCreative && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Vector3 direction = -_camera.GlobalBasis.Z;
            if (_miningDirection.LengthSquared() < 0.1f || direction.Dot(_miningDirection) < 0.992f)
            {
                _miningDirection = direction;
                _miningProgress = 0f;
            }
            bool hasTarget = _planet.TryGetTargetBlockType(_camera.GlobalPosition, direction, out int targetBlock);
            string tool = _inventory.SelectedItem;
            bool stoneAllowed = targetBlock != 2 || tool is "Primitive Pickaxe" or "Stone Pickaxe";
            bool primitiveToolAllowed = tool != "Primitive Pickaxe" || targetBlock == 2;
            float miningDuration = targetBlock == 2
                ? tool == "Stone Pickaxe" ? 0.42f : 1.05f
                : 0.85f;
            if (!hasTarget || !stoneAllowed || !primitiveToolAllowed)
            {
                _miningProgress = 0f;
                _miningLabel.Text = targetBlock == 2 ? "STONE REQUIRES A PICKAXE"
                    : tool == "Primitive Pickaxe" ? "PRIMITIVE PICKAXE: STONE ONLY" : "";
                return;
            }
            _miningProgress += delta;
            _miningLabel.Text = $"MINING  {Mathf.Clamp(_miningProgress / miningDuration * 100f, 0f, 100f):0}%";
            if (_miningProgress >= miningDuration)
            {
                if (_inventory.SelectedItem == "Stick")
                {
                    float hit = _planet.GetRayHitDistance(_camera.GlobalPosition, direction, 5.5f);
                    if (hit < 5.5f)
                        _survival.SpawnPickup("Pebble", 1, _camera.GlobalPosition + direction * Mathf.Max(0.4f, hit - 0.12f));
                }
                else if (_planet.TryEdit(_camera.GlobalPosition, direction, -1))
                {
                    _inventory.AddBlock(_planet.LastBrokenBlockType);
                    if (_planet.LastBrokenBlockType == 2 && tool is "Primitive Pickaxe" or "Stone Pickaxe")
                        _inventory.ConsumeToolUse(tool);
                    SoundManager.Play(SoundKind.BlockBreak);
                }
                _miningProgress = 0f;
            }
        }
        else { _miningProgress = 0f; _miningLabel.Text = ""; }

        // Footsteps are updated after physics movement, not here. Running this
        // in both loops caused the old machine-gun "TSHT" cadence.
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_dead) return;
        if (@event.IsActionPressed("jump") && @event is not InputEventKey { Echo: true })
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastJumpTapAt <= 0.32)
            {
                if (!GameSession.IsCreative) { _lastJumpTapAt = -10.0; return; }
                _flying = !_flying;
                Velocity = Vector3.Zero;
                _flightLabel.Visible = _flying;
                _lastJumpTapAt = -10.0;
            }
            else _lastJumpTapAt = now;
        }

        if (@event is InputEventKey flashlightKey && flashlightKey.Pressed
            && !flashlightKey.Echo && flashlightKey.Keycode == Key.G)
        {
            _flashlight.Visible = !_flashlight.Visible;
            _flashlightModel.Visible = _flashlight.Visible;
        }

        if (@event is InputEventKey viewKey && viewKey.Pressed && !viewKey.Echo && viewKey.Keycode == Key.F5)
        {
            _viewMode = (_viewMode + 1) % 3;
            _bodyMesh.Visible = _viewMode != 0;
            UpdateCameraPlacement();
        }

        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateObjectLocal(Vector3.Up, -motion.Relative.X * MouseSensitivity);
            // Almost the full vertical range: enough to mine directly below or
            // above without allowing the camera basis to flip upside down.
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.562f, 1.562f);
            _pivot.Rotation = new Vector3(_pitch, 0, 0);
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _miningHeld = false;
            return;
        }
        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed
            && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                _handSwing = 1f;
                Vector3 direction = -_camera.GlobalBasis.Z;
                if (_monsters.TryHit(_camera.GlobalPosition, direction)) return;
                if (_mobs.TryHit(_camera.GlobalPosition, direction)) return;
                if (_survival.TryBreakPlacedObject(_camera.GlobalPosition, direction)) return;
                NatureSystem.ChopResult tree = _nature.TryChop(_camera.GlobalPosition, direction,
                    GameSession.IsCreative || _inventory.SelectedItem is "Axe" or "Stone Axe", out Vector3 woodDrop);
                if (tree == NatureSystem.ChopResult.Chopped)
                {
                    _survival.SpawnPickup("Wood", 4, woodDrop);
                    if (_inventory.SelectedItem is "Axe" or "Stone Axe") _inventory.ConsumeToolUse(_inventory.SelectedItem);
                    return;
                }
                if (tree == NatureSystem.ChopResult.NeedsAxe)
                {
                    SoundManager.Play(SoundKind.Tree, -16f);
                    return;
                }
                if (GameSession.IsCreative)
                {
                    if (_planet.TryEdit(_camera.GlobalPosition, direction, -1)) SoundManager.Play(SoundKind.BlockBreak);
                }
                else
                {
                    _miningHeld = true;
                    _miningDirection = direction;
                    _miningProgress = 0f;
                }
            }
            else if (mouseButton.ButtonIndex == MouseButton.Right)
            {
                string item = _inventory.SelectedItem;
                Vector3 spawnDirection = (GlobalPosition - _camera.GlobalBasis.Z * 3f).Normalized();
                Vector3 direction = -_camera.GlobalBasis.Z;
                if (item == "Shears" && GetNode<MobManager>("../MobManager").TryShear(_camera.GlobalPosition, direction, out bool woolProduced))
                {
                    if (woolProduced) _inventory.ConsumeToolUse("Shears");
                }
                else if (_survival.TryInteract(_camera.GlobalPosition, direction)) { }
                else if (!GameSession.IsCreative && _survival.UseSelected(spawnDirection)) { }
                else if (GameSession.IsCreative && (item == "Campfire" || item == "Bed")
                    && _survival.UseSelected(spawnDirection)) { }
                else if (item.EndsWith("Egg") && _inventory.ConsumeSelected())
                {
                    bool spawned = item is "Chicken Egg" or "Cow Egg" or "Sheep Egg"
                        ? GetNode<MobManager>("../MobManager").SpawnEgg(item.StartsWith("Cow") ? "Cow" : item.StartsWith("Sheep") ? "Sheep" : "Chicken", spawnDirection)
                        : GetNode<NightMonsterManager>("../NightMonsterManager").SpawnEgg(item, spawnDirection);
                    if (!spawned && !GameSession.IsCreative) _inventory.AddSelectedItem();
                }
                else if (_inventory.SelectedBlockType >= 0 && _inventory.ConsumeSelected())
                {
                    if (!_planet.TryEdit(_camera.GlobalPosition, -_camera.GlobalBasis.Z, 1, _inventory.SelectedBlockType) && !GameSession.IsCreative)
                        _inventory.AddBlock(_inventory.SelectedBlockType);
                    else SoundManager.Play(SoundKind.BlockPlace);
                }
            }
            _inventory.Refresh();
        }
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        float delta = (float)deltaValue;
        if (_dead)
        {
            Velocity = Vector3.Zero;
            return;
        }
        if (Input.MouseMode != Input.MouseModeEnum.Captured) { Velocity = Vector3.Zero; return; }
        Vector3 up = GlobalPosition.Normalized();
        AlignToPlanet(up);

        Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        float speed = MoveSpeed;
        if (_flying) speed *= 2.5f;
        else if (Input.IsPhysicalKeyPressed(Key.Shift)) speed *= 1.65f;
        Vector3 desired = (GlobalTransform.Basis.X * input.X + GlobalTransform.Basis.Z * input.Y).Normalized() * speed;

        if (_flying)
        {
            float vertical = (Input.IsActionPressed("jump") ? 1.0f : 0.0f)
                           - (Input.IsPhysicalKeyPressed(Key.Shift) ? 1.0f : 0.0f);
            Vector3 flightTarget = desired + up * vertical * speed;
            Velocity = Velocity.MoveToward(flightTarget, Acceleration * delta);
            UpDirection = up;
            MoveAndSlide();
            UpdateCameraPlacement();
            UpdateFlashlight(delta);
            return;
        }

        Vector3 radialVelocity = up * Velocity.Dot(up);
        Vector3 tangentVelocity = Velocity - radialVelocity;
        tangentVelocity = tangentVelocity.MoveToward(desired, Acceleration * delta);

        bool grounded = IsOnFloor() || _surfaceGrounded;
        if (grounded)
        {
            radialVelocity = Vector3.Zero;
            if (Input.IsActionJustPressed("jump")) radialVelocity = up * JumpSpeed;
        }
        else
        {
            radialVelocity -= up * Gravity * delta;
        }

        Velocity = tangentVelocity + radialVelocity;
        UpDirection = up;
        bool wasGrounded = _surfaceGrounded;
        MoveAndSlide();
        _surfaceGrounded = IsOnFloor();
        if (!_surfaceGrounded)
            _airbornePeakRadius = Mathf.Max(_airbornePeakRadius, GlobalPosition.Length());
        else if (!wasGrounded)
        {
            float fallDistance = _airbornePeakRadius - GlobalPosition.Length();
            float safeDistance = _planet.BlockHeight * 5f;
            if (fallDistance > safeDistance && !GameSession.IsCreative)
                ApplyDamage((fallDistance - safeDistance) * 12f);
            _airbornePeakRadius = GlobalPosition.Length();
        }
        else _airbornePeakRadius = GlobalPosition.Length();
        // Concave chunk collisions are streamed and replaced at runtime. Keep
        // a radial floor guard permanently active so a one-frame seam can
        // never turn into an endless fall. Walls and ceilings remain entirely
        // handled by MoveAndSlide; this guard only corrects inward penetration.
        KeepAboveProceduralSurface();
        UpdateFootsteps();
        UpdateCameraPlacement();
        UpdateFlashlight(delta);
    }

    private void AlignToPlanet(Vector3 up)
    {
        Vector3 forward = -GlobalTransform.Basis.Z;
        forward = (forward - up * forward.Dot(up)).Normalized();
        if (forward.LengthSquared() < 0.01f) forward = up.Cross(Vector3.Right).Normalized();
        Vector3 right = forward.Cross(up).Normalized();
        GlobalBasis = new Basis(right, up, -forward).Orthonormalized();
    }

    private void KeepAboveProceduralSurface()
    {
        Vector3 direction = GlobalPosition.Normalized();
        float feetRadius = GlobalPosition.Length() - GroundClearance;
        float floorRadius = _planet.FloorRadius(direction, feetRadius + MaxStepHeight);
        float minimumRadius = floorRadius + GroundClearance;
        float currentRadius = GlobalPosition.Length();
        _surfaceGrounded = currentRadius <= minimumRadius + 0.12f;

        float inwardSpeed = Velocity.Dot(direction);

        if (currentRadius >= minimumRadius) return;

        GlobalPosition = direction * minimumRadius;
        if (inwardSpeed < 0.0f)
            Velocity -= direction * inwardSpeed;
    }

    private void MoveAcrossVoxelSurface(Vector3 tangentVelocity, Vector3 radialVelocity, float delta)
    {
        Vector3 tangentMove = tangentVelocity * delta;
        int steps = Mathf.Max(1, Mathf.CeilToInt(tangentMove.Length() / (BodyRadius * 0.42f)));
        Vector3 tangentStep = tangentMove / steps;
        for (int step = 0; step < steps; step++)
        {
            Vector3 currentDirection = GlobalPosition.Normalized();
            float currentFeet = GlobalPosition.Length() - GroundClearance;
            float currentSurface = _planet.FloorRadius(currentDirection, currentFeet + MaxStepHeight);
            int currentObstructions = BodyObstructionCount(GlobalPosition, currentFeet);
            Vector3 candidate = GlobalPosition + tangentStep;
            if (CanOccupy(candidate, currentFeet, currentSurface, tangentStep, currentObstructions))
            {
                GlobalPosition = candidate;
                continue;
            }

            // Resolve a diagonal collision one axis at a time. Only the clear
            // component survives, which creates sliding without penetration.
            Vector3 moveX = GlobalBasis.X * tangentStep.Dot(GlobalBasis.X);
            Vector3 moveZ = GlobalBasis.Z * tangentStep.Dot(GlobalBasis.Z);
            Vector3 first = moveX.LengthSquared() >= moveZ.LengthSquared() ? moveX : moveZ;
            Vector3 second = first == moveX ? moveZ : moveX;
            if (CanOccupy(GlobalPosition + first, currentFeet, currentSurface, first, currentObstructions))
            {
                GlobalPosition += first;
            }
            else if (CanOccupy(GlobalPosition + second, currentFeet, currentSurface, second, currentObstructions))
            {
                GlobalPosition += second;
            }
            else
            {
                Velocity -= tangentVelocity;
                break;
            }
        }

        Vector3 currentDirectionAfterSlide = GlobalPosition.Normalized();
        Vector3 radialMove = currentDirectionAfterSlide * radialVelocity.Dot(currentDirectionAfterSlide) * delta;
        Vector3 radialCandidate = GlobalPosition + radialMove;
        float radialFeet = radialCandidate.Length() - GroundClearance;
        if (radialMove.Dot(currentDirectionAfterSlide) <= 0f || BodyObstructionCount(radialCandidate, radialFeet) == 0)
            GlobalPosition = radialCandidate;
        else
            Velocity -= currentDirectionAfterSlide * Velocity.Dot(currentDirectionAfterSlide);
    }

    private bool CanOccupy(Vector3 candidate, float feetRadius, float currentSurface,
        Vector3 movement, int currentObstructions)
    {
        Vector3 up = candidate.Normalized();
        float candidateSurface = _planet.FloorRadius(up, feetRadius + MaxStepHeight);
        if (candidateSurface > currentSurface + MaxStepHeight && feetRadius < candidateSurface)
            return false;

        int candidateObstructions = BodyObstructionCount(candidate, feetRadius);
        return candidateObstructions == 0
            || (currentObstructions > 0 && candidateObstructions < currentObstructions);
    }

    private int BodyObstructionCount(Vector3 centre, float feetRadius)
    {
        Vector3 right = GlobalBasis.X * BodyRadius;
        Vector3 forward = -GlobalBasis.Z * BodyRadius;
        Vector3 diagonalA = (right + forward).Normalized() * BodyRadius;
        Vector3 diagonalB = (right - forward).Normalized() * BodyRadius;
        Vector3[] samples = [centre, centre + right, centre - right, centre + forward, centre - forward,
            centre + diagonalA, centre - diagonalA, centre + diagonalB, centre - diagonalB];
        int blocked = 0;
        foreach (Vector3 sample in samples)
            if (!_planet.HasRoom(sample.Normalized(), feetRadius, BodyHeight)) blocked++;
        return blocked;
    }

    private void MoveFlyingWithCollisions(float delta)
    {
        Vector3 move = Velocity * delta;
        int steps = Mathf.Max(1, Mathf.CeilToInt(move.Length() / (BodyRadius * 0.42f)));
        Vector3 movementStep = move / steps;
        for (int step = 0; step < steps; step++)
        {
            Vector3 up = GlobalPosition.Normalized();
            int currentObstructions = BodyObstructionCount(
                GlobalPosition, GlobalPosition.Length() - GroundClearance);
            Vector3 candidate = GlobalPosition + movementStep;
            int obstruction = BodyObstructionCount(candidate, candidate.Length() - GroundClearance);
            if (obstruction == 0 || (currentObstructions > 0 && obstruction < currentObstructions))
            {
                GlobalPosition = candidate;
                continue;
            }

            Vector3 radialStep = up * movementStep.Dot(up);
            Vector3 tangentStep = movementStep - radialStep;
            Vector3 tangentCandidate = GlobalPosition + tangentStep;
            int tangentObstruction = BodyObstructionCount(tangentCandidate,
                tangentCandidate.Length() - GroundClearance);
            if (tangentObstruction == 0 || (currentObstructions > 0 && tangentObstruction < currentObstructions))
                GlobalPosition = tangentCandidate;
            else
                Velocity -= Velocity - up * Velocity.Dot(up);

            Vector3 radialCandidate = GlobalPosition + radialStep;
            int radialObstruction = BodyObstructionCount(radialCandidate,
                radialCandidate.Length() - GroundClearance);
            if (radialObstruction == 0 || (currentObstructions > 0 && radialObstruction < currentObstructions))
                GlobalPosition = radialCandidate;
            else
                Velocity -= up * Velocity.Dot(up);
        }
    }

    private void UpdateCameraPlacement()
    {
        if (_viewMode == 0)
        {
            // The eye point is seven centimetres below the top of the capsule,
            // so the FPS camera can never protrude beyond the player's hitbox.
            _camera.Position = Vector3.Zero;
            _camera.Rotation = Vector3.Zero;
            return;
        }

        bool selfie = _viewMode == 2;
        Vector3 desiredLocal = selfie ? new Vector3(0f, 0.28f, -4.2f) : new Vector3(0f, 0.28f, 4.2f);
        Vector3 origin = _pivot.GlobalPosition;
        Vector3 desiredWorld = _pivot.ToGlobal(desiredLocal);
        Vector3 ray = desiredWorld - origin;
        float hitDistance = _planet.GetRayHitDistance(origin, ray.Normalized(), ray.Length());
        float allowedDistance = Mathf.Min(ray.Length(), Mathf.Max(0.25f, hitDistance - 0.12f));
        _camera.Position = desiredLocal.Normalized() * allowedDistance;
        _camera.Rotation = selfie ? new Vector3(0f, Mathf.Pi, 0f) : Vector3.Zero;
    }

    private void BuildFirstPersonHands()
    {
        _hands = new Node3D { Name = "FirstPersonHands", Position = new Vector3(0f, -0.32f, -0.58f) };
        _camera.AddChild(_hands);
        StandardMaterial3D skin = new() { AlbedoColor = new Color(0.72f, 0.48f, 0.31f), Roughness = 0.9f };
        var handMesh = new BoxMesh { Size = new Vector3(0.16f, 0.18f, 0.42f), Material = skin };
        _leftShoulder = new Node3D { Name = "LeftShoulder", Position = new Vector3(-0.3f, 0.04f, 0.12f) };
        _rightShoulder = new Node3D { Name = "RightShoulder", Position = new Vector3(0.3f, 0.04f, 0.12f) };
        _hands.AddChild(_leftShoulder); _hands.AddChild(_rightShoulder);
        _leftShoulder.AddChild(new MeshInstance3D
        {
            Name = "LeftHand", Mesh = handMesh, Position = new Vector3(0f, -0.09f, -0.2f)
        });
        _rightShoulder.AddChild(new MeshInstance3D
        {
            Name = "RightHand", Mesh = handMesh, Position = new Vector3(0f, -0.09f, -0.2f)
        });
        _heldItem = new MeshInstance3D { Name = "HeldItem", Position = new Vector3(0f, -0.18f, -0.38f) };
        _leftShoulder.AddChild(_heldItem);
        _flashlightRig.Reparent(_rightShoulder, false);
        _flashlightRig.Position = new Vector3(0f, -0.12f, -0.39f);
        _flashlightRig.Rotation = Vector3.Zero;
    }

    private void UpdateFirstPersonHands(float delta)
    {
        _hands.Visible = _viewMode == 0 && !_dead;
        string selected = _inventory.SelectedItem;
        if (_renderedHeldItem != selected)
        {
            _renderedHeldItem = selected;
            Color colour = selected.EndsWith("Block") && _inventory.SelectedBlockType >= 0
                ? BlockCatalog.Get(_inventory.SelectedBlockType).Color
                : selected.Contains("Stone") || selected == "Pebble" ? new Color(0.48f,0.5f,0.53f)
                : new Color(0.43f,0.24f,0.08f);
            _heldItem.Mesh = string.IsNullOrEmpty(selected) ? null : new BoxMesh
            {
                Size = selected.EndsWith("Block") ? Vector3.One * 0.22f : new Vector3(0.09f, 0.38f, 0.09f),
                Material = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f }
            };
        }
        // The selected item stays in the left hand while the flashlight uses
        // the right, so both can be visible at the same time.
        _heldItem.Visible = _heldItem.Mesh != null;
        _handSwing = Mathf.MoveToward(_handSwing, 0f, delta * 5.5f);
        float swing = Mathf.Sin(_handSwing * Mathf.Pi) * 0.85f;
        // Each limb rotates at its own shoulder; the shoulder position itself
        // never moves, so the arm cannot detach during a strike.
        _leftShoulder.Rotation = new Vector3(-0.24f - swing * 0.72f, 0.12f + swing * 0.22f, -0.08f + swing * 0.18f);
        _rightShoulder.Rotation = new Vector3(-0.24f, -0.12f, 0.08f);
    }

    private void UpdateFootsteps()
    {
        float moved = GlobalPosition.DistanceTo(_lastFootstepPosition);
        bool walking = !_flying && _surfaceGrounded && moved > 0.0005f && Velocity.Length() > 0.7f;
        RegisterFootstepTravel(moved, walking, Input.IsPhysicalKeyPressed(Key.Shift));
        _lastFootstepPosition = GlobalPosition;
    }

    private void RegisterFootstepTravel(float moved, bool groundedAndMoving, bool sprinting)
    {
        if (groundedAndMoving)
        {
            _footstepDistance += moved;
            float stride = sprinting ? 1.18f : 1.58f;
            if (_footstepDistance >= stride)
            {
                _footstepDistance -= stride;
                SoundManager.Play(SoundKind.Footstep, -18f);
                FootstepSoundCount++;
            }
        }
        else _footstepDistance = 0f;
    }

    public void RegisterFootstepTravelForValidation(float moved, bool groundedAndMoving, bool sprinting)
        => RegisterFootstepTravel(moved, groundedAndMoving, sprinting);

    public void ApplyDamage(float amount)
    {
        if (GameSession.IsCreative || _dead) return;
        Health = Mathf.Max(0f, Health - amount);
        UpdateHealthLabel();
        if (Health > 0f) return;
        _survival.DropOnDeath();
        _music.PlayDeathMusic(false);
        _dead = true;
        _miningHeld = false;
        Velocity = Vector3.Zero;
        _deathLabel.Visible = false;
        _deathMenu.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void Respawn()
    {
        _dead = false;
        Health = 100f;
        if (GameSession.Current is { } world) world.FoodPoisoned = false;
        GlobalPosition = _survival.TryGetSafeBedRespawn(out Vector3 bedRespawn)
            ? bedRespawn
            : Vector3.Up * (_planet.SurfaceRadius(Vector3.Up) + GroundClearance + 1.2f);
        _airbornePeakRadius = GlobalPosition.Length();
        _deathLabel.Visible = false;
        _deathMenu.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        // A Button may restore the visible cursor at the end of the same GUI
        // event that invoked Respawn. Capture it again on the next frame.
        GetTree().ProcessFrame += CaptureMouseAfterRespawn;
        _music.ResumeWorldMusic();
        UpdateHealthLabel();
    }

    private void CaptureMouseAfterRespawn()
    {
        GetTree().ProcessFrame -= CaptureMouseAfterRespawn;
        if (!_dead) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void BuildDeathMenu()
    {
        _deathMenu = new Control { Name = "DeathMenu", Visible = false };
        _deathMenu.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0.12f, 0.005f, 0.008f, 0.88f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _deathMenu.AddChild(dim);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _deathMenu.AddChild(center);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(420, 220), Alignment = BoxContainer.AlignmentMode.Center };
        center.AddChild(layout);
        var title = new Label { Text = "YOU DIED", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", new Color(0.95f, 0.12f, 0.1f));
        var respawn = new Button { Name = "Respawn", Text = "Respawn", CustomMinimumSize = new Vector2(360, 54) };
        var menu = new Button { Name = "ReturnToMenu", Text = "Return to menu", CustomMinimumSize = new Vector2(360, 54) };
        respawn.Pressed += Respawn;
        menu.Pressed += () => GetNode<Main>("..").SaveAndReturnToMenu();
        layout.AddChild(title); layout.AddChild(respawn); layout.AddChild(menu);
        GetNode<CanvasLayer>("../HUD").AddChild(_deathMenu);
    }

    public void RestoreHealth(float amount)
    {
        if (_dead) return;
        Health = Mathf.Min(100f, Health + amount);
        UpdateHealthLabel();
    }

    public void OnGameModeChanged()
    {
        if (!GameSession.IsCreative) { _flying = false; _flightLabel.Visible = false; }
        if (GameSession.IsCreative) Health = 100f;
        UpdateHealthLabel();
        _inventory.Refresh();
    }

    private void UpdateHealthLabel()
    {
        _healthLabel.Visible = !GameSession.IsCreative;
        int full = Mathf.Clamp(Mathf.CeilToInt(Health / 10f), 0, 10);
        _healthLabel.Text = new string('♥', full) + new string('♡', 10 - full);
    }


    private void UpdateFlashlight(float delta)
    {
        if (!_flashlight.Visible) return;
        _flashlightProbeCooldown -= delta;
        if (_flashlightProbeCooldown > 0f) return;
        _flashlightProbeCooldown = 0.1f;

        float hitDistance = _planet.GetRayHitDistance(
            _flashlight.GlobalPosition, -_flashlight.GlobalBasis.Z, _flashlight.SpotRange);
        float nearWallFactor = Mathf.SmoothStep(0.45f, 2.4f, hitDistance);
        _flashlight.LightEnergy = Mathf.Lerp(0.3f, 5.0f, nearWallFactor);
    }
}
