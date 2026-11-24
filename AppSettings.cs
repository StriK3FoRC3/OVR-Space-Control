using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record AppSettings
{
    public static AppSettings FreshDefaults => new()
    {
        Mode = TeleportMode.SpaceDrag,
        Gravity3D = 0,
        FlightGravity = 0,
        FlightResistance = 80,
        FlySpeed = .25m,
        FlyTopSpeed = 0m,
        FlyAcceleration = 2m,
        HoverBobbingIntensity = 15,
        FlyPitchOffset = -15,
        FlyYawOffset = -5,
        FlyDirection = DragPivot.RightController,
        EditorDrag = false,
        ResistanceLocked = true,
        SpinResistance = 100,
        PlaySpaceCenterColor = 2949375,
        PlaySpaceCenterTransparency = 60,
        ScalePlaySpaceCenterWithDistance = true,
        SaveRotation = false,
        WindowPosition = new Point(1768, 195)
    };

    public TeleportMode Mode { get; init; }
    public bool Enabled { get; init; } = true;
    public CircleControls Circle { get; init; } = CircleControls.Default;
    public SweepControls Sweep { get; init; } = SweepControls.Default;
    public bool ZeroGravity { get; init; }
    public int? Gravity3D { get; init; }
    public int SpaceDragResistance { get; init; } = 100;
    public int SpaceDragGravity { get; init; }
    public int? FlightGravity { get; init; }
    public int? FlightResistance { get; init; }
    public decimal FlySpeed { get; init; } = 2m;
    public decimal FlyTopSpeed { get; init; } = 15m;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FlyTopSpeedEnabled { get; init; }
    public decimal FlyAcceleration { get; init; }
    public int FlyRotationSmoothing { get; init; } = 35;
    public bool DirectFlight { get; init; }
    public bool IdleHoverBobbing { get; init; }
    public int HoverBobbingIntensity { get; init; } = 4;
    public int FlyPitchOffset { get; init; }
    public int FlyYawOffset { get; init; }
    public bool ShowFlightDirectionLine { get; init; } = true;
    public DragPivot FlyDirection { get; init; } = DragPivot.Headset;
    public bool EditorDrag { get; init; } = true;
    public bool EditorDrag3D { get; init; }
    public bool KeepSpaceDragActive { get; init; }
    public DragPivot RotationPivot { get; init; } = DragPivot.Headset;
    public bool SpaceDragRotate { get; init; }
    public bool SpaceDragLockHeight { get; init; }
    public bool SpaceDragFloorBounce { get; init; }
    public int SpaceDragFloorOffsetCm { get; init; } = 70;
    public bool SpaceDragCeilingBounce { get; init; }
    public int SpaceDragCeilingCm { get; init; } = 300;
    public bool CeilingBounce { get; init; }
    public int CeilingCm { get; init; } = 300;
    public decimal TurnStrength { get; init; }
    public int TurnDragSteps { get; init; } = 4;
    public bool ResistanceLocked { get; init; }
    public int? SpinResistance { get; init; }
    public int FloorOffsetCm { get; init; } = 70;
    public bool FloorBounce { get; init; }
    public int GlideResistance { get; init; } = 100;
    public DragControls Drag { get; init; } = DragControls.Default;
    public TurnControls Turn { get; init; } = TurnControls.Default;
    public bool ShowPlaySpaceCenter { get; init; }
    public int PlaySpaceCenterColor { get; init; } = 0xffffff;
    public int PlaySpaceCenterTransparency { get; init; }
    public int PlaySpaceCenterSize { get; init; } = 100;
    public bool ScalePlaySpaceCenterWithDistance { get; init; }
    public bool ShowCoordinates { get; init; } = true;
    public bool SaveHeight { get; init; }
    public bool SaveRotation { get; init; } = true;
    public bool CloseToTray { get; init; }
    public Point? WindowPosition { get; init; }
    public bool StartMinimized { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool LockDragHeight { get; init; }
}

internal sealed class SettingsFile(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<TeleportMode>() }
    };

    public static SettingsFile ForAppFolder() => new(Path.Combine(AppContext.BaseDirectory, "settings.json"));

    public AppSettings Load()
    {
        AppSettings settings;
        try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? AppSettings.FreshDefaults; }
        catch (FileNotFoundException) { return AppSettings.FreshDefaults; }
        catch (DirectoryNotFoundException) { return AppSettings.FreshDefaults; }

        return settings with
        {
            Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : TeleportMode.Normal,
            Circle = settings.Circle with
            {
                Radius = Math.Clamp(settings.Circle.Radius, .25m, 1000m),
                Points = Math.Clamp(settings.Circle.Points, 3, 360),
                IntervalMs = Math.Clamp(settings.Circle.IntervalMs, 10, 60000)
            },
            Sweep = settings.Sweep with
            {
                Width = Math.Clamp(settings.Sweep.Width, .25m, 1000m),
                Rows = Math.Clamp(settings.Sweep.Rows, 1, SweepControls.MaxRows),
                RowDistance = settings.Sweep.RowDistance is decimal distance ? SweepControls.SnapRowDistance(distance) : null,
                PointsPerRow = Math.Clamp(settings.Sweep.PointsPerRow, 2, 32),
                IntervalMs = Math.Clamp(settings.Sweep.IntervalMs, 10, 60000)
            },
            Drag = settings.Drag with
            {
                Multiplier = Math.Max(.25m, settings.Drag.Multiplier),
                AccelerationStrength = Math.Max(0m, settings.Drag.AccelerationStrength)
            },
            Turn = settings.Turn with { Multiplier = Math.Max(.25m, settings.Turn.Multiplier),
                TransitionMs = Math.Clamp(settings.Turn.TransitionMs <= 0 ? TurnControls.Default.TransitionMs : settings.Turn.TransitionMs, 10, 60000) },
        };
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
