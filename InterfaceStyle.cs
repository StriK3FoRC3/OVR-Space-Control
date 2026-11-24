using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

internal static class InterfaceStyle
{
    public static readonly Color Background = Color.FromArgb(17, 17, 17);
    public static readonly Color Card = Color.FromArgb(28, 28, 28);
    public static readonly Color Button = Color.FromArgb(43, 43, 43);
    public static readonly Color Text = Color.FromArgb(241, 241, 241);
    public static readonly Color Muted = Color.FromArgb(163, 163, 163);
    public static readonly Color Purple = Color.FromArgb(114, 0, 211);
    public static readonly Color PointerPurple = Color.FromArgb(192, 112, 255);
    public static readonly Color PointerRed = Color.FromArgb(255, 65, 76);
    public static readonly string LogoPath = Path.Combine(AppContext.BaseDirectory, "SteamVR", "dashboard-icon.png");

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    public static void MatchTitleBar(nint window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        const int useImmersiveDarkMode = 20;
        DwmSetWindowAttribute(window, useImmersiveDarkMode, 1, sizeof(int));
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics graphics, RectangleF bounds, float radius, Color color)
    {
        using var path = Rounded(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }


}

internal sealed class TrayMenuRenderer() : ToolStripProfessionalRenderer(new TrayMenuColors())
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        InterfaceStyle.FillRounded(e.Graphics, new RectangleF(0, 0, e.Item.Width - 1, e.Item.Height - 1), 5,
            Color.FromArgb(57, 39, 72));
    }

    private sealed class TrayMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => InterfaceStyle.Card;
        public override Color MenuBorder => Color.FromArgb(66, 61, 72);
        public override Color SeparatorDark => Color.FromArgb(66, 61, 72);
        public override Color SeparatorLight => InterfaceStyle.Card;
    }
}

internal enum TeleportMode { Normal, Circle, SpaceDrag, Custom, Sweep, ControllerTurn, Fly }
internal enum DashboardAction
{
    None, Normal, Circle, SpaceDrag, Custom, Toggle, Reset, RadiusDown, RadiusUp, PointsDown, PointsUp,
    IntervalDown, IntervalUp, Order, MultiplierDown, MultiplierUp, StrengthDown, StrengthUp, TurnDown, TurnUp, ToggleTurn,
    SaveRotation, EditName, FinishName, CustomRecord, CustomDrag, LockHeight, CancelCustom, DeletePattern, ConfirmDelete, PageUp, PageDown, Backspace, CustomIntervalDown, CustomIntervalUp, Settings, Coordinates, SteamStartup, CloseToTray, MinimizeToTray, StartMinimized, CustomRepeat, CustomOrder, UndoPoint, OpenDashboard, Bindings, SaveHeight, Sweep, SweepPointsDown, SweepPointsUp, SweepDirection, RowDistanceDown, RowDistanceUp, ControllerTurn, DriverInstall, ResistanceDown, ResistanceUp, ZeroGravity, TurnDragDown, TurnDragUp, TurnStrengthDown, TurnStrengthUp, SpinDown, SpinUp, Bounce, FloorOffsetDown, FloorOffsetUp, ResistanceLock, GravityDown, GravityUp, CeilingToggle, CeilingDown, CeilingUp, DragHeightLock, DragRotate, DragWalls, KeepDragActive, ShowDragGrids, PatternTransition, Fly, FlySpeedDown, FlySpeedUp, FlyDirection, FlyTopSpeedDown, FlyTopSpeedUp, FlyAccelerationDown, FlyAccelerationUp, FlySmoothingDown, FlySmoothingUp, FlyPitchDown, FlyPitchUp, FlyYawDown, FlyYawUp, FlightDirectionLine, DirectFlight, IdleHoverBobbing, HoverIntensityDown, HoverIntensityUp, PatternRow = 100, CustomSmooth = 200, EditPoints, DonePoints, AddPoint, PointScroll, PlaySpaceCenter,
    RotationPivot = 290, PointCell = 300, PickCenterColor = 400, ScaleCenterWithDistance, CenterColorChannel = 500
}

internal enum DragPivot { RightController, LeftController, Headset, FirstTracker }

internal readonly record struct CircleControls(decimal Radius, int Points, int IntervalMs, bool Random, bool SmoothTransition = false)
{
    public static CircleControls Default => new(3, 10, 200, false);
}

internal readonly record struct SweepControls(decimal Width, int Rows, int IntervalMs, int PointsPerRow = 8, bool Random = false, bool Vertical = false, decimal? RowDistance = null, bool Stacked = false, bool SmoothTransition = false)
{
    public const int MaxRows = 7;
    public int Layers => Stacked ? Rows : 1;
    public const decimal RowDistanceStep = .25m;
    public decimal EffectiveRowDistance => RowDistance ?? 1.5m;

    public static decimal SnapRowDistance(decimal distance) =>
        Math.Round(Math.Clamp(distance, RowDistanceStep, 1000m) / RowDistanceStep, MidpointRounding.AwayFromZero)
            * RowDistanceStep;
    public static SweepControls Default => new(10, 2, 200, PointsPerRow: 5);
}

internal readonly record struct PanelState(bool Enabled, TeleportMode Mode, CircleControls Settings, string Status,
    bool Connected, DragControls Drag = default, Vector3 Offset = default, TurnControls Turn = default,
    CustomEditorState Custom = default, bool SettingsOpen = false, bool ShowCoordinates = true, bool DashboardAvailable = false, bool AutoLaunch = false, bool CloseToTray = false, bool MinimizeToTray = false, bool StartMinimized = false, SweepControls Sweep = default, bool DriverReady = false, bool DriverRegistered = false, string? DriverMessage = null, bool DriverBusy = false, int Resistance = 100, bool ZeroGravity = false, int TurnDragSteps = 4, decimal TurnStrength = 0m, int SpinResistance = 100, bool Bounce = false, int FloorOffsetCm = 70, bool ResistanceLocked = false, int Gravity = 0, bool HeightLocked = false, bool Ceiling = false, int CeilingCm = 300, Vector3? HeadRotation = null, string ConnectionStatus = "Waiting for SteamVR.", bool DragRotate = false, int WallCount = 0, bool PlacingWalls = false, bool KeepDragActive = false, bool ShowDragGrids = false, bool DesktopBindingsNotice = false, DragPivot RotationPivot = DragPivot.Headset, bool ShowPlaySpaceCenter = false, int PlaySpaceCenterColor = 0xffffff, bool CenterColorPickerOpen = false, int PlaySpaceCenterTransparency = 0, int PlaySpaceCenterSize = 100, bool ScalePlaySpaceCenterWithDistance = false, decimal FlySpeed = 2m, DragPivot FlyDirection = DragPivot.Headset, decimal FlyTopSpeed = 15m, decimal FlyAcceleration = 0m, int FlyRotationSmoothing = 35, int FlyPitchOffset = 0, int FlyYawOffset = 0,
    bool ShowFlightDirectionLine = true, bool DirectFlight = false, bool IdleHoverBobbing = false, int HoverBobbingIntensity = 4);

internal readonly record struct DashboardPointerFrame(Point? Position, Rectangle HoverBounds, byte HoverOpacity);

internal sealed class DashboardPanel : IDisposable
{
    public const int Width = 1024, Height = 840;
    private static readonly Rectangle Preferences = new(822, 768, 170, 48);
    private static readonly Rectangle Bindings = new(210, 768, 215, 48);
    private static readonly Rectangle OpenDashboard = new(32, 768, 164, 48);
    private static readonly (string Label, DashboardAction Action, Rectangle Bounds)[] StartupSettings =
    [
        ("Start with SteamVR", DashboardAction.SteamStartup, new(520, 295, 92, 48)),
        ("Close to tray", DashboardAction.CloseToTray, new(520, 365, 92, 48)),
        ("Minimize to tray", DashboardAction.MinimizeToTray, new(520, 435, 92, 48)),
        ("Start minimized", DashboardAction.StartMinimized, new(520, 505, 92, 48))
    ];
    private static readonly Rectangle DriverInstall = new(670, 137, 230, 45);
    private static readonly Rectangle RotationPivot = new(670, 590, 290, 48);
    private static readonly Rectangle Coordinates = new(868, 295, 92, 48);
    private static readonly Rectangle PlaySpaceCenter = new(868, 435, 92, 48);
    private static readonly Rectangle PickCenterColor = new(820, 505, 140, 48);
    private static readonly Rectangle FlightDirectionLine = new(868, 365, 92, 48);
    private static readonly Rectangle ScaleCenterWithDistance = new(744, 555, 156, 48);
    private static readonly Rectangle CloseColorPicker = new(744, 640, 156, 48);
    private static Rectangle ColorChannel(int channel) => new(475, 220 + channel * 65, 375, 34);
    public const int TransparencyChannel = 3;
    public const int SizeChannel = 4;
    public const int ColorChannelCount = 5;
    public const int MinCenterSize = 25, MaxCenterSize = 200;
    public const int ColorChannelValues = 256;
    internal static DashboardAction CenterChannelAction(int channel, float x)
    {
        var bounds = ColorChannel(channel);
        int max = channel switch
        {
            TransparencyChannel => 100,
            SizeChannel => MaxCenterSize - MinCenterSize,
            _ => 255
        };
        int value = Math.Clamp((int)MathF.Round((x - bounds.Left) * max / (bounds.Width - 1)), 0, max);
        return DashboardAction.CenterColorChannel + channel * ColorChannelValues + value;
    }

    private const int ModeControlHeight = 48;
    private static readonly Rectangle KeepDrag = new(504, 140, 221, ModeControlHeight);
    private static readonly Rectangle Toggle = new(754, 140, 206, ModeControlHeight);
    private static readonly Rectangle Reset = new(754, 205, 206, ModeControlHeight);
    private static readonly Rectangle Order = new(286, 580, 206, ModeControlHeight);
    private static readonly Rectangle TransitionTimeMinus = Minus(520) with { Y = 580 };
    private static readonly Rectangle TransitionTimePlus = Plus(520) with { Y = 580 };
    private static readonly Rectangle Transition = new(754, 580, 206, ModeControlHeight);
    private static readonly Rectangle SweepDirection = new(520, 580, 206, ModeControlHeight);
    private static readonly Rectangle TurnToggle = new(520, 395, 206, ModeControlHeight);
    private static readonly (string Label, TeleportMode Mode, DashboardAction Action)[] Modes =
    [
        ("Space Drag", TeleportMode.SpaceDrag, DashboardAction.SpaceDrag),
        ("3D Space Drag", TeleportMode.ControllerTurn, DashboardAction.ControllerTurn),
        ("Flight Mode", TeleportMode.Fly, DashboardAction.Fly),
        ("Teleport", TeleportMode.Normal, DashboardAction.Normal),
        ("Circle Teleport", TeleportMode.Circle, DashboardAction.Circle),
        ("Sweep Teleport", TeleportMode.Sweep, DashboardAction.Sweep)
    ];
    private static readonly Rectangle CustomTab = new(754, 35, 206, ModeControlHeight);
    public const int PatternsPerPage = 3;
    private static readonly Rectangle DeletePattern = new(754, 270, 206, ModeControlHeight);
    private static readonly Rectangle ConfirmDelete = new(286, 395, 206, ModeControlHeight);

    private static IEnumerable<(Rectangle Bounds, string Label, DashboardAction Action, bool Selected)> CustomControls(CustomEditorState editor)
    {
        if (editor.EditPoint >= 0 && editor.Pattern is { } pattern)
        {
            yield return (new(810, 655, 150, ModeControlHeight), "Done", DashboardAction.DonePoints, false);
            if (pattern.Points.Length < 256)
                yield return (new(286, 655, 150, ModeControlHeight), "Add point", DashboardAction.AddPoint, false);
            for (int row = 0; row < Math.Min(PointRows, pattern.Points.Length - editor.EditPoint); row++)
            for (int field = 0; field < PointColumns; field++)
                yield return (PointCellBounds(row, field), pattern.CellText(editor.EditPoint + row, field),
                    DashboardAction.PointCell + row * PointColumns + field, false);
            yield break;
        }
        if (editor.Selected < 0)
            yield return (new(440, 295, 520, ModeControlHeight), string.IsNullOrEmpty(editor.Name) ? (editor.Naming ? "Type a name" : "Enter name (optional)") : editor.Name,
                DashboardAction.EditName, editor.Naming);
        if (editor.Selected >= 0)
        {
            if (editor.ConfirmDelete)
            {
                yield return (ConfirmDelete, "Delete", DashboardAction.ConfirmDelete, true);
                yield return (new(520, 395, 206, ModeControlHeight), "Cancel", DashboardAction.CancelCustom, false);
            }
            else if (editor.Naming)
            {
                yield return (new(286, 270, 674, ModeControlHeight), string.IsNullOrEmpty(editor.Name) ? "Type a name" : editor.Name,
                    DashboardAction.EditName, true);
                yield return (new(286, 395, 206, ModeControlHeight), "Save", DashboardAction.FinishName, false);
                yield return (new(520, 395, 206, ModeControlHeight), "Cancel", DashboardAction.CancelCustom, false);
            }
            else
            {
                yield return (new(754, 213, 206, ModeControlHeight), "Edit points", DashboardAction.EditPoints, false);
                yield return (new(520, 458, 206, ModeControlHeight), editor.Pattern?.SmoothTransition == true ? "Smooth Transition" : "Teleport",
                    DashboardAction.CustomSmooth, editor.Pattern?.SmoothTransition == true);
                yield return (new(520, 270, 206, ModeControlHeight), "Rename", DashboardAction.EditName, false);
                yield return (DeletePattern, "Delete", DashboardAction.DeletePattern, false);
                yield return (new(520, 395, 206, ModeControlHeight), editor.Repeat ? "On" : "Off", DashboardAction.CustomRepeat, editor.Repeat);
                yield return (new(754, 395, 206, ModeControlHeight), editor.RandomOrder ? "Random" : "Sequential", DashboardAction.CustomOrder, false);
                yield return (Minus(286), "-", DashboardAction.CustomIntervalDown, false);
                yield return (Plus(286), "+", DashboardAction.CustomIntervalUp, false);
            }
            yield break;
        }

        yield return (new(286, 450, 206, ModeControlHeight), editor.SaveRotation ? "Save rotation on" : "Save rotation off", DashboardAction.SaveRotation, editor.SaveRotation);
        yield return (new(520, 450, 206, ModeControlHeight), editor.SaveHeight ? "Save height on" : "Save height off", DashboardAction.SaveHeight, editor.SaveHeight);
        if (editor.PointCount > 0)
            yield return (new(754, 450, 206, ModeControlHeight), "Undo last point", DashboardAction.UndoPoint, false);
        yield return (new(286, 365, 206, ModeControlHeight), editor.Recording ? "Finish recording" : "Record", DashboardAction.CustomRecord, editor.Recording);
        yield return (new(520, 365, 206, ModeControlHeight), editor.Drag ? (editor.Drag3D ? "3D Space Drag" : "Space Drag") : "Drag off", DashboardAction.CustomDrag, editor.Drag);
        yield return (new(754, 365, 206, ModeControlHeight), "Lock drag height", DashboardAction.LockHeight, editor.LockHeight);
    }

    private static IEnumerable<(Rectangle Bounds, string Label, DashboardAction Action, bool Selected)> SavedControls(CustomEditorState editor)
    {
        if (editor.Names is not { Length: > 0 } names) yield break;
        for (int row = 0; row < PatternsPerPage; row++)
        {
            int index = editor.Page * PatternsPerPage + row;
            if (index >= names.Length) break;
            yield return (new(48, 510 + row * 46, 178, 41), names[index],
                DashboardAction.PatternRow + row, editor.Selected == index);
        }
        if (editor.Page > 0) yield return (new(48, 650, 82, 28), "‹", DashboardAction.PageDown, false);
        if ((editor.Page + 1) * PatternsPerPage < names.Length)
            yield return (new(144, 650, 82, 28), "›", DashboardAction.PageUp, false);
    }

    private static readonly (int X, string Label, DashboardAction Down, DashboardAction Up)[] Steppers =
    [
        (286, "Radius (m)", DashboardAction.RadiusDown, DashboardAction.RadiusUp),
        (520, "Points", DashboardAction.PointsDown, DashboardAction.PointsUp),
        (754, "Interval (ms)", DashboardAction.IntervalDown, DashboardAction.IntervalUp)
    ];

    private static readonly (int X, string Label, DashboardAction Down, DashboardAction Up)[] SweepSteppers =
    [
        (286, "Width (m)", DashboardAction.RadiusDown, DashboardAction.RadiusUp),
        (520, "Rows", DashboardAction.PointsDown, DashboardAction.PointsUp),
        (754, "Points per row", DashboardAction.SweepPointsDown, DashboardAction.SweepPointsUp),
        (286, "Row gap (m)", DashboardAction.RowDistanceDown, DashboardAction.RowDistanceUp),
        (520, "Interval (ms)", DashboardAction.IntervalDown, DashboardAction.IntervalUp)
    ];

    private static Rectangle PatternMinus(int x, int index) => Minus(x) with { Y = index >= 3 ? 485 : 395 };
    private static Rectangle PatternPlus(int x, int index) => Plus(x) with { Y = index >= 3 ? 485 : 395 };

    private static Rectangle Mode(int index) => new(42, 150 + index * 60, 190, 55);
    private static Rectangle Minus(int x) => new(x, 395, 48, ModeControlHeight);
    private static Rectangle Plus(int x) => new(x + 158, 395, 48, ModeControlHeight);
    private readonly Bitmap logo = new(InterfaceStyle.LogoPath);
    private readonly Bitmap vrFrame = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly byte[] pixels = new byte[Width * Height * 4];
    public Bitmap Frame { get; } = new(Width, Height, PixelFormat.Format32bppArgb);

    public static DashboardAction HitTest(PointF point, PanelState state) => HitTarget(point, state).Action;

    public static (DashboardAction Action, Rectangle Bounds) HitTarget(PointF point, PanelState state)
    {
        var p = Point.Truncate(point);
        for (int i = 0; i < Modes.Length; i++)
            if (Mode(i).Contains(p)) return (Modes[i].Action, Mode(i));
        if (Preferences.Contains(p)) return (DashboardAction.Settings, Preferences);
        if (Bindings.Contains(p) && state.Connected) return (DashboardAction.Bindings, Bindings);
        if (OpenDashboard.Contains(p) && state.DashboardAvailable) return (DashboardAction.OpenDashboard, OpenDashboard);
        if (CustomTab.Contains(p)) return (DashboardAction.Custom, CustomTab);
        foreach (var (bounds, _, action, _) in SavedControls(state.Custom))
            if (bounds.Contains(p)) return (action, bounds);
        if (state.SettingsOpen)
        {
            if (state.CenterColorPickerOpen)
            {
                if (ScaleCenterWithDistance.Contains(p)) return (DashboardAction.ScaleCenterWithDistance, ScaleCenterWithDistance);
                if (CloseColorPicker.Contains(p)) return (DashboardAction.PickCenterColor, CloseColorPicker);
                for (int channel = 0; channel < ColorChannelCount; channel++)
                {
                    var bounds = ColorChannel(channel);
                    if (!bounds.Contains(p)) continue;
                    return (CenterChannelAction(channel, p.X), bounds);
                }
                return default;
            }
            if (!state.DriverBusy && DriverInstall.Contains(p)) return (DashboardAction.DriverInstall, DriverInstall);
            foreach (var (_, action, bounds) in StartupSettings)
                if (bounds.Contains(p) && (action != DashboardAction.SteamStartup || state.Connected))
                    return (action, bounds);
            if (RotationPivot.Contains(p)) return (DashboardAction.RotationPivot, RotationPivot);
            if (PickCenterColor.Contains(p)) return (DashboardAction.PickCenterColor, PickCenterColor);
            if (PlaySpaceCenter.Contains(p)) return (DashboardAction.PlaySpaceCenter, PlaySpaceCenter);
            if (FlightDirectionLine.Contains(p)) return (DashboardAction.FlightDirectionLine, FlightDirectionLine);
            if (Coordinates.Contains(p)) return (DashboardAction.Coordinates, Coordinates);
            return default;
        }
        var toggle = Toggle;
        if (state.Mode != TeleportMode.Custom && toggle.Contains(p)) return (DashboardAction.Toggle, toggle);
        if (state.Mode != TeleportMode.Custom && state.Connected && Reset.Contains(p)) return (DashboardAction.Reset, Reset);
        if (state.Mode == TeleportMode.Custom)
        {
            if (state.Custom is { EditPoint: >= 0, PointCount: > PointRows } && PointScrollTrack.Contains(p))
                return (DashboardAction.PointScroll, PointScrollTrack);
            foreach (var (bounds, _, action, _) in CustomControls(state.Custom))
                if (bounds.Contains(p)) return (action, bounds);
            return default;
        }
        if (state.Mode == TeleportMode.Normal)
        {
            if (Transition.Contains(p)) return (DashboardAction.PatternTransition, Transition);
            if (state.Turn.SmoothTransition)
            {
                if (TransitionTimeMinus.Contains(p)) return (DashboardAction.IntervalDown, TransitionTimeMinus);
                if (TransitionTimePlus.Contains(p)) return (DashboardAction.IntervalUp, TransitionTimePlus);
            }
            if (TurnToggle.Contains(p)) return (DashboardAction.ToggleTurn, TurnToggle);
            if (!state.Turn.Enabled) return default;
            if (Minus(286).Contains(p)) return (DashboardAction.TurnDown, Minus(286));
            if (Plus(286).Contains(p)) return (DashboardAction.TurnUp, Plus(286));
            return default;
        }
        if (state.Mode is TeleportMode.SpaceDrag or TeleportMode.ControllerTurn or TeleportMode.Fly)
        {
            if (state.Mode == TeleportMode.SpaceDrag && KeepDrag.Contains(p))
                return (DashboardAction.KeepDragActive, KeepDrag);
            foreach (var control in SpaceDragControls(state))
                if (control.Action != DashboardAction.None && control.Enabled && control.Bounds.Contains(p))
                    return (control.Action, control.Bounds);
            return default;
        }
        if (state.Mode is not (TeleportMode.Circle or TeleportMode.Sweep)) return default;
        bool sweep = state.Mode == TeleportMode.Sweep;
        if (Transition.Contains(p)) return (DashboardAction.PatternTransition, Transition);
        var order = Order;
        if (order.Contains(p)) return (DashboardAction.Order, order);
        if (sweep && SweepDirection.Contains(p)) return (DashboardAction.SweepDirection, SweepDirection);
        var steppers = sweep ? SweepSteppers : Steppers;
        for (int i = 0; i < steppers.Length; i++)
        {
            var (x, _, down, up) = steppers[i];
            var minus = PatternMinus(x, i);
            var plus = PatternPlus(x, i);
            if (minus.Contains(p)) return (down, minus);
            if (plus.Contains(p)) return (up, plus);
        }
        return default;
    }

    public void Render(PanelState state)
    {
        using var g = Graphics.FromImage(Frame);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(InterfaceStyle.Background);
        g.DrawImage(logo, 34, 24, 66, 66);
        Text(g, "OVR Space Control", new(117, 15, 475, 54), 39, InterfaceStyle.Text, bold: true);
        string offset = state.HeadRotation.HasValue
            ? $"X {state.Offset.X:0.00}   Y {state.Offset.Y:0.00}   Z {state.Offset.Z:0.00} m"
            : "X —   Y —   Z —";
        if (state.ShowCoordinates)
        {
            Text(g, offset, new(119, 63, 510, 24), 18, InterfaceStyle.Muted);
            if (state.HeadRotation is Vector3 angles)
                Text(g, $"Yaw {angles.Y:0.0}°   Pitch {angles.X:0.0}°   Roll {angles.Z:0.0}°", new(119, 86, 510, 22), 17, InterfaceStyle.Muted);
        }
        InterfaceStyle.FillRounded(g, new Rectangle(32, 112, 210, 635), 14, InterfaceStyle.Card);
        InterfaceStyle.FillRounded(g, new Rectangle(258, 112, 734, 635), 14, InterfaceStyle.Card);
        Text(g, "MODES", new(49, 120, 174, 25), 19, InterfaceStyle.Muted, bold: true);
        for (int i = 0; i < Modes.Length; i++)
            NavigationItem(g, Mode(i), Modes[i].Label, !state.SettingsOpen && state.Mode == Modes[i].Mode);
        Button(g, CustomTab, "Custom Pattern", !state.SettingsOpen && state.Mode == TeleportMode.Custom && state.Custom.Selected < 0);
        foreach (var (bounds, label, _, selected) in SavedControls(state.Custom))
            NavigationItem(g, bounds, label, !state.SettingsOpen && selected);
        Button(g, OpenDashboard, "Open in VR", available: state.DashboardAvailable);
        Button(g, Bindings, "Controller bindings", available: state.Connected);
        Text(g, state.Connected ? "SteamVR connected" : "SteamVR offline",
            new(Bindings.Right + 12, Bindings.Top, Preferences.Left - Bindings.Right - 24, state.Connected && !state.DesktopBindingsNotice ? Bindings.Height : 24),
            19, state.Connected ? Color.FromArgb(0x4D, 0xFF, 0x4D) : InterfaceStyle.Muted);
        if (state.DesktopBindingsNotice)
            Text(g, "Controller bindings open on the desktop.", new(Bindings.Right + 12, Bindings.Top + 25, Preferences.Left - Bindings.Right - 24, 25), 15, Color.Gold);
        else if (!state.Connected)
            Text(g, state.ConnectionStatus, new(Bindings.Right + 12, Bindings.Top + 25, Preferences.Left - Bindings.Right - 24, 25), 15, InterfaceStyle.Muted);
        Button(g, Preferences, "Settings", state.SettingsOpen);
        if (state.SettingsOpen)
        {
            if (state.CenterColorPickerOpen)
            {
                DrawCenterColorPicker(g, state.PlaySpaceCenterColor, state.PlaySpaceCenterTransparency, state.PlaySpaceCenterSize, state.ScalePlaySpaceCenterWithDistance);
                return;
            }
            Button(g, DriverInstall, state.DriverRegistered ? "Remove VR driver" : "Install VR driver", available: !state.DriverBusy);
            Text(g, state.DriverMessage ?? (state.DriverReady ? "3D rotation driver connected" : state.DriverRegistered ? "Driver registered. Restart SteamVR to load it." : "Optional driver adds pitch and roll to 3D Space Drag."), new(286, 182, 614, 22), 16, InterfaceStyle.Muted);
            Text(g, "Settings", new(286, 141, 614, 48), 32, InterfaceStyle.Text, bold: true);
            Text(g, "Startup and window", new(286, 240, 326, 38), 24, InterfaceStyle.Text, bold: true);
            Text(g, "VR display and controls", new(670, 240, 290, 38), 24, InterfaceStyle.Text, bold: true);
            Text(g, "Movement offsets", new(670, 295, 190, 48), 20, InterfaceStyle.Text);
            Text(g, "Flight direction line", new(670, 365, 190, 48), 20, InterfaceStyle.Text);
            Button(g, FlightDirectionLine, state.ShowFlightDirectionLine ? "On" : "Off", state.ShowFlightDirectionLine);
            foreach (var (label, action, bounds) in StartupSettings)
            {
                bool enabled = action switch
                {
                    DashboardAction.SteamStartup => state.AutoLaunch,
                    DashboardAction.CloseToTray => state.CloseToTray,
                    DashboardAction.StartMinimized => state.StartMinimized,
                    _ => state.MinimizeToTray
                };
                bool available = action != DashboardAction.SteamStartup || state.Connected;
                Text(g, label, new(286, bounds.Y + 6, 224, 40), 20, InterfaceStyle.Text);
                Button(g, bounds, available ? (enabled ? "On" : "Off") : "Offline", enabled && available, available);
            }
            Text(g, "Space Drag rotation pivot", new(670, 555, 290, 32), 20, InterfaceStyle.Text);
            Button(g, RotationPivot, state.RotationPivot switch
            {
                DragPivot.LeftController => "Left controller",
                DragPivot.Headset => "Headset",
                DragPivot.RightController => "Right controller",
                _ => $"Tracker {(int)state.RotationPivot - (int)DragPivot.FirstTracker + 1}"
            });
            Text(g, "Play space center", new(670, 435, 190, 48), 20, InterfaceStyle.Text);
            Button(g, PlaySpaceCenter, state.ShowPlaySpaceCenter ? "On" : "Off", state.ShowPlaySpaceCenter);
            Text(g, "Center color", new(670, 505, 118, 48), 20, InterfaceStyle.Text);
            InterfaceStyle.FillRounded(g, new Rectangle(785, 517, 24, 24), 6, OpaqueRgb(state.PlaySpaceCenterColor));
            Button(g, PickCenterColor, "Choose color");
            Button(g, Coordinates, state.ShowCoordinates ? "On" : "Off", state.ShowCoordinates);
            return;
        }
        if (state.Mode == TeleportMode.Custom)
        {
            DrawCustomEditor(g, state);
            return;
        }
        var (heading, instructions) = state.Mode switch
        {
            TeleportMode.Circle => ("Circle Teleport", "Hold R3 and aim at the center.\nRelease to start. Press R3 to stop."),
            TeleportMode.Sweep => ("Sweep Teleport", "Hold R3 and aim at the center.\nRelease to start. Press R3 to stop."),
            TeleportMode.ControllerTurn => ("3D Space Drag", "Hold R3 to move and turn your play space.\nRelease R3 to let go."),
            TeleportMode.Fly => ("Flight Mode", state.DirectFlight ? "Hold R3 to fly where you point.\nRelease to let go." : "Hold R3. Point left or right to turn.\nPoint up or down to climb or descend."),
            TeleportMode.SpaceDrag => ("Space Drag", "Hold R3 and pull with your controller.\nRelease to let go."),
            _ => ("Teleport", "Hold R3 and aim at the floor.\nRelease when the ring is purple.")
        };
        Text(g, heading, new(286, 141, 410, 44), 32, InterfaceStyle.Text, bold: true);
        Text(g, instructions, new(286, 191, 450, 62), 21, InterfaceStyle.Text);
        Text(g, "L3 cancels a held R3 action or resets your play space.", new(286, 277, 620, 29), 20, InterfaceStyle.Muted);
        string feature = state.Mode switch
        {
            TeleportMode.Circle => "Circle Teleport",
            TeleportMode.Sweep => "Sweep Teleport",
            TeleportMode.ControllerTurn => "3D Space Drag",
            TeleportMode.SpaceDrag => "Space Drag",
            TeleportMode.Fly => "Flight Mode",
            _ => "Teleport"
        };
        if (state.Mode == TeleportMode.SpaceDrag)
            Button(g, KeepDrag, "Keep active", state.KeepDragActive);
        Button(g, Toggle, feature + (state.Enabled ? " on" : " off"), state.Enabled);
        Button(g, Reset, "Reset play space", available: state.Connected);
        using var divider = new Pen(InterfaceStyle.Button, 2);
        g.DrawLine(divider, 286, 318, 900, 318);
        if (state.Mode is TeleportMode.Circle or TeleportMode.Sweep) DrawPatternSettings(g, state);
        if (state.Mode == TeleportMode.Normal) DrawTurnSettings(g, state.Turn);
        if (state.Mode is TeleportMode.SpaceDrag or TeleportMode.ControllerTurn or TeleportMode.Fly)
        {
            if (state.Mode == TeleportMode.ControllerTurn)
                Text(g, "Warning: 3D Space Drag can cause motion sickness.", new(286, 253, 614, 24), 19, Color.Gold);
            else if (state.KeepDragActive)
                Text(g, "In teleport modes, hold R3 away from the floor to drag.", new(286, 253, 450, 24), 17, InterfaceStyle.Muted);
            foreach (var control in SpaceDragControls(state))
            {
                if (control.Action == DashboardAction.None)
                    Text(g, control.Label, control.Bounds, 18, InterfaceStyle.Text, centered: true);
                else if (control.Action == DashboardAction.ResistanceLock)
                {
                    Button(g, control.Bounds, "", control.Selected && control.Enabled, available: control.Enabled);
                    using var pen = new Pen(control.Enabled ? InterfaceStyle.Text : InterfaceStyle.Muted, 2.4f);
                    int x = control.Bounds.X + 12, y = control.Bounds.Y + 10;
                    g.DrawArc(pen, x + (control.Selected ? 3 : 10), y, 13, 15, 180, 180);
                    g.DrawLine(pen, x + 3 + (control.Selected ? 0 : 7), y + 7, x + 3 + (control.Selected ? 0 : 7), y + 12);
                    if (control.Selected) g.DrawLine(pen, x + 16, y + 7, x + 16, y + 12);
                    g.DrawRectangle(pen, x, y + 12, 19, 13);
                }
                else Button(g, control.Bounds, control.Label, control.Selected && control.Enabled, available: control.Enabled);
            }
            if (state.Mode == TeleportMode.ControllerTurn && !state.DriverReady)
                Text(g, "Space Drag fallback is active. Install the VR driver\nin Settings for pitch and roll.", new(286, 683, 674, 57), 20, Color.Gold);
        }
        if (state.Connected && (state.Mode != TeleportMode.ControllerTurn || state.DriverReady)) Text(g, state.Status, new(286, 713, 670, 27), 18, InterfaceStyle.Muted);
    }

    private static string GridButtonText(PanelState state) => state.ShowDragGrids ? "Hide boundaries" : "Show boundaries";

    private static string WallButtonText(PanelState state) => state.PlacingWalls ? "Cancel" :
        state.WallCount == 4 ? "Remove walls" : "Place walls";

    private static IEnumerable<(Rectangle Bounds, string Label, DashboardAction Action, bool Enabled, bool Selected)> SpaceDragControls(PanelState state)
    {
        bool is3D = state.Mode == TeleportMode.ControllerTurn;
        bool fly = state.Mode == TeleportMode.Fly;
        var settings = new List<(int X, int Y, string Label, string Value, DashboardAction Down, DashboardAction Up, bool Enabled)>
        {
            (286, 330, fly ? "Starting speed (m/s)" : "Drag multiplier", fly ? $"{state.FlySpeed:0.##}" : $"{(is3D ? state.TurnDragSteps * .25m : state.Drag.Multiplier):0.##}x",
                fly ? DashboardAction.FlySpeedDown : is3D ? DashboardAction.TurnDragDown : DashboardAction.MultiplierDown,
                fly ? DashboardAction.FlySpeedUp : is3D ? DashboardAction.TurnDragUp : DashboardAction.MultiplierUp, true),
            (fly ? 754 : 520, 330, fly ? "Acceleration (m/s²)" : "Acceleration strength", fly ? $"{state.FlyAcceleration:0.##}" : $"{(is3D ? state.TurnStrength : state.Drag.AccelerationStrength):0.##}x",
                fly ? DashboardAction.FlyAccelerationDown : is3D ? DashboardAction.TurnStrengthDown : DashboardAction.StrengthDown,
                fly ? DashboardAction.FlyAccelerationUp : is3D ? DashboardAction.TurnStrengthUp : DashboardAction.StrengthUp, true),
            (286, 425, "Gravity", $"{state.Gravity}%", DashboardAction.GravityDown, DashboardAction.GravityUp, true),
            (520, 425, "Movement resistance", $"{state.Resistance}%", DashboardAction.ResistanceDown, DashboardAction.ResistanceUp, true)
        };
        if (fly)
        {
            settings.Add((520, 330, "Top speed (m/s)", state.FlyTopSpeed > 0 ? $"{state.FlyTopSpeed:0.##}" : "Off", DashboardAction.FlyTopSpeedDown, DashboardAction.FlyTopSpeedUp, true));
            bool controllerSteering = state.FlyDirection != DragPivot.Headset;
            settings.Add((286, 603, "Pitch offset", controllerSteering ? $"{state.FlyPitchOffset:+0;-0;0}°" : "—", DashboardAction.FlyPitchDown, DashboardAction.FlyPitchUp, controllerSteering));
            settings.Add((520, 603, "Yaw offset", controllerSteering ? $"{state.FlyYawOffset:+0;-0;0}°" : "—", DashboardAction.FlyYawDown, DashboardAction.FlyYawUp, controllerSteering));
            settings.Add((754, 603, "Bobbing intensity", $"{state.HoverBobbingIntensity} cm", DashboardAction.HoverIntensityDown, DashboardAction.HoverIntensityUp, state.IdleHoverBobbing));
            settings.Add((754, 425, "Rotation smoothing", $"{state.FlyRotationSmoothing}%", DashboardAction.FlySmoothingDown, DashboardAction.FlySmoothingUp, true));
        }
        else
        {
            settings.Add((520, 520, "Floor offset (cm)", $"{state.FloorOffsetCm}", DashboardAction.FloorOffsetDown, DashboardAction.FloorOffsetUp, true));
            settings.Add((520, 603, "Ceiling height (cm)", $"{state.CeilingCm}", DashboardAction.CeilingDown, DashboardAction.CeilingUp, true));
        }
        if (is3D)
            settings.Add((754, 425, "Rotation resistance", $"{state.SpinResistance}%", DashboardAction.SpinDown, DashboardAction.SpinUp, true));

        foreach (var (x, y, label, value, down, up, enabled) in settings)
        {
            yield return (new(x, y, 206, 27), label, DashboardAction.None, false, false);
            int minusX = down == DashboardAction.SpinDown ? x + 10 : x;
            int plusX = is3D && up == DashboardAction.ResistanceUp ? x + 143 : x + 158;
            yield return (new(minusX, y + 31, 48, ModeControlHeight), "-", down, enabled, false);
            yield return (new(minusX + 48, y + 31, plusX - minusX - 48, ModeControlHeight), value, DashboardAction.None, false, false);
            yield return (new(plusX, y + 31, 48, ModeControlHeight), "+", up, enabled, false);
        }

        if (is3D)
            yield return (new(715, 456, 45, ModeControlHeight), "", DashboardAction.ResistanceLock, true, state.ResistanceLocked);
        else if (!fly)
        {
            yield return (new(754, 361, 206, ModeControlHeight), state.HeightLocked ? "Height locked" : "Lock height", DashboardAction.DragHeightLock, true, state.HeightLocked);
            yield return (new(754, 456, 206, ModeControlHeight), state.DragRotate ? "Rotation on" : "Rotation off", DashboardAction.DragRotate, true, state.DragRotate);
        }
        if (fly)
        {
            yield return (new(520, 551, 206, ModeControlHeight), state.DirectFlight ? "Direct flight on" : "Direct flight off",
                DashboardAction.DirectFlight, true, state.DirectFlight);
            yield return (new(754, 551, 206, ModeControlHeight), "Idle hover bobbing",
                DashboardAction.IdleHoverBobbing, true, state.IdleHoverBobbing);
            yield return (new(286, 520, 206, 27), "Fly direction", DashboardAction.None, false, false);
            yield return (new(286, 551, 206, ModeControlHeight), state.FlyDirection switch
            {
                DragPivot.LeftController => "Left controller",
                DragPivot.RightController => "Right controller",
                _ => "Headset"
            }, DashboardAction.FlyDirection, true, false);
        }
        if (fly) yield break;
        yield return (new(754, 551, 206, ModeControlHeight), GridButtonText(state), DashboardAction.ShowDragGrids, true, state.ShowDragGrids);
        yield return (new(754, 634, 206, ModeControlHeight), WallButtonText(state), DashboardAction.DragWalls, true, state.PlacingWalls);
        yield return (new(286, 634, 206, ModeControlHeight), state.Ceiling ? "Ceiling bounce on" : "Ceiling bounce off", DashboardAction.CeilingToggle, true, state.Ceiling);
        yield return (new(286, 551, 206, ModeControlHeight), state.Bounce ? "Floor bounce on" : "Floor bounce off", DashboardAction.Bounce, true, state.Bounce);
    }
    public const int PointRows = 8, PointColumns = 7;

    public static Rectangle PointCellBounds(int row, int field) => new(335 + field * 87, 287 + row * 44, 84, 38);

    public static readonly Rectangle PointScrollTrack = new(948, 287, 14, PointRows * 44 - 6);

    public static Rectangle PointScrollThumb(int first, int count)
    {
        var track = PointScrollTrack;
        int height = Math.Max(30, track.Height * PointRows / count);
        int offset = (int)Math.Round(first * (track.Height - height) / (double)(count - PointRows));
        return new(track.X + 2, track.Y + offset, track.Width - 4, height);
    }

    private static void DrawCustomEditor(Graphics g, PanelState state)
    {
        var editor = state.Custom;
        string heading = editor.Selected < 0 ? "Custom pattern" : editor.Naming ? "Rename pattern" : editor.Name ?? "";
        string instructions =
            editor.EditPoint >= 0 ? "Select a cell to enter a value. A dash skips that value.\nTravel time applies only in Smooth Transition." :
            editor.ConfirmDelete ? "Delete this pattern permanently?" :
            editor.Selected >= 0 && editor.Naming ? "Enter the new name, then choose Done or Save." :
            editor.Selected >= 0 ? "Hold R3 to aim. Release to start at the pointer.\nPress R3 to stop. L3 resets your play space." :
            "Center your play space before recording.\nHold R3 to drag with Space Drag on. Press L3 to record a point.";
        Text(g, "Currently a work in progress and may not work as expected.", new(286, 114, 690, 30), 24, Color.Gold, bold: true);
        Text(g, heading, new(286, 144, 614, 41), 32, InterfaceStyle.Text, bold: true);
        Text(g, instructions, new(286, 186, editor.Selected >= 0 && editor.EditPoint < 0 ? 463 : 674, 62), editor.Selected < 0 ? 18 : 22, InterfaceStyle.Text);
        if (editor.Selected < 0)
            Text(g, editor.HeadsetPose ?? "Headset tracking unavailable", new(286, 250, 674, 27), 18, InterfaceStyle.Muted);
        if (editor.Selected < 0) Text(g, "Pattern name", new(286, 295, 152, 48), 21, InterfaceStyle.Muted);
        if (editor.EditPoint >= 0 && editor.Pattern is { } rows)
        {
            if (rows.Points.Length > PointRows)
            {
                InterfaceStyle.FillRounded(g, PointScrollTrack, 6, Color.FromArgb(42, 42, 42));
                InterfaceStyle.FillRounded(g, PointScrollThumb(editor.EditPoint, rows.Points.Length), 5, Color.FromArgb(110, 110, 110));
            }
        }
        foreach (var (bounds, label, action, selected) in CustomControls(editor))
        {
            if (action != DashboardAction.DeletePattern)
            {
                Button(g, bounds, label, selected);
                continue;
            }
            Button(g, bounds, "");
            using var pen = new Pen(InterfaceStyle.Text, 2);
            int x = bounds.Left + 15, y = bounds.Top + 15;
            g.DrawRectangle(pen, x + 2, y + 3, 13, 15);
            g.DrawLine(pen, x, y + 2, x + 17, y + 2);
            g.DrawLine(pen, x + 6, y - 1, x + 11, y - 1);
            Text(g, label, new(bounds.Left + 34, bounds.Top, bounds.Width - 40, bounds.Height), 20, InterfaceStyle.Text, centered: true);
        }
        if (editor.EditPoint >= 0 && editor.Pattern is { } pattern)
        {
            string[] labels = ["X (m)", "Y (m)", "Z (m)", "Yaw (°)", "Pitch (°)", "Roll (°)", "Time (ms)"];
            for (int field = 0; field < labels.Length; field++)
            {
                var cell = PointCellBounds(0, field);
                Text(g, labels[field], new(cell.Left, 252, cell.Width, 31), 18, InterfaceStyle.Muted, centered: true);
            }
            int visible = Math.Min(PointRows, pattern.Points.Length - editor.EditPoint);
            for (int row = 0; row < visible; row++)
            {
                var cell = PointCellBounds(row, 0);
                Text(g, $"{editor.EditPoint + row + 1}", new(286, cell.Top, 43, cell.Height), 18, InterfaceStyle.Muted, centered: true);
            }
        }
        else if (editor.Selected >= 0 && !editor.ConfirmDelete && !editor.Naming)
        {
            Text(g, editor.PointCount == 1 ? "1 saved point" : $"{editor.PointCount} saved points", new(286, 281, 340, 32), 20, InterfaceStyle.Muted);
            Text(g, "Repeat pattern", new(520, 365, 206, 26), 20, InterfaceStyle.Muted);
            Text(g, "Point order", new(754, 365, 206, 26), 20, InterfaceStyle.Muted);
            Text(g, "Interval (ms)", new(286, 365, 170, 26), 20, InterfaceStyle.Muted);
            Text(g, $"{editor.IntervalMs}", new(334, 395, 110, ModeControlHeight), 22, InterfaceStyle.Text, centered: true);
        }
        else if (editor.Selected < 0)
            Text(g, editor.PointCount == 1 ? "1 point recorded" : $"{editor.PointCount} points recorded", new(286, 535, 368, 33), 20, InterfaceStyle.Muted);
        if (state.Connected) Text(g, state.Status, new(286, 713, 674, 27), 16, InterfaceStyle.Muted);
    }

    private static void DrawTurnSettings(Graphics g, TurnControls settings)
    {
        Text(g, "SPACE TURN", new(286, 333, 614, 24), 19, InterfaceStyle.Muted, bold: true);
        Text(g, "Turn multiplier", new(286, 366, 181, 26), 21, InterfaceStyle.Muted);
        Button(g, Minus(286), "−", available: settings.Enabled);
        Button(g, Plus(286), "+", available: settings.Enabled);
        Text(g, $"{settings.Multiplier:0.##}×", new(334, 395, 110, ModeControlHeight), 25,
            settings.Enabled ? InterfaceStyle.Text : InterfaceStyle.Muted, centered: true);
        Text(g, "Movement", new(754, 550, 206, 30), 21, InterfaceStyle.Muted);
        Button(g, Transition, settings.SmoothTransition ? "Smooth Transition" : "Teleport", settings.SmoothTransition);
        if (settings.SmoothTransition)
        {
            Text(g, "Travel time (ms)", new(520, 550, 206, 30), 21, InterfaceStyle.Muted);
            Button(g, TransitionTimeMinus, "-");
            Button(g, TransitionTimePlus, "+");
            Text(g, $"{settings.TransitionMs}", new(TransitionTimeMinus.Right, TransitionTimeMinus.Top,
                TransitionTimePlus.Left - TransitionTimeMinus.Right, ModeControlHeight),
                25, InterfaceStyle.Text, centered: true);
        }
        Button(g, TurnToggle, settings.Enabled ? "Space Turn on" : "Space Turn off", settings.Enabled);
        Text(g, "After a teleport, the right stick turns your play space in place.\nR3 teleports again. L3 resets and returns the stick to the game.",
            new(286, 475, 674, 59), 18, InterfaceStyle.Muted);
    }

    private static void DrawPatternSettings(Graphics g, PanelState state)
    {
        bool sweep = state.Mode == TeleportMode.Sweep;
        Text(g, sweep ? "SWEEP PATTERN" : "CIRCLE PATTERN", new(286, 333, 614, 24), 19, InterfaceStyle.Muted, bold: true);
        var settings = state.Settings;
        string[] values = sweep
            ? [$"{state.Sweep.Width:0.##}", $"{state.Sweep.Rows}", $"{state.Sweep.PointsPerRow}", $"{state.Sweep.EffectiveRowDistance:0.##}", $"{state.Sweep.IntervalMs}"]
            : [$"{settings.Radius:0.##}", $"{settings.Points}", $"{settings.IntervalMs}"];
        var steppers = sweep ? SweepSteppers : Steppers;
        for (int i = 0; i < steppers.Length; i++)
        {
            var (x, label, _, _) = steppers[i];
            var minus = PatternMinus(x, i);
            var plus = PatternPlus(x, i);
            Text(g, label, new(x, minus.Top - 32, 206, 32), 21, InterfaceStyle.Muted);
            Button(g, minus, "−");
            Button(g, plus, "+");
            Text(g, values[i], new(minus.Right, minus.Top, plus.Left - minus.Right, minus.Height), 25, InterfaceStyle.Text, centered: true);
        }
        bool smooth = sweep ? state.Sweep.SmoothTransition : settings.SmoothTransition;
        Text(g, "Movement", new(754, 550, 206, 26), 21, InterfaceStyle.Muted);
        Button(g, Transition, smooth ? "Smooth Transition" : "Teleport", smooth);
        Text(g, "Point order", new(286, 550, 206, 26), 21, InterfaceStyle.Muted);
        Button(g, Order, (sweep ? state.Sweep.Random : settings.Random) ? "Random" : "Sequential");
        if (sweep)
        {
            Text(g, "Row direction", new(520, 550, 206, 26), 21, InterfaceStyle.Muted);
            Button(g, SweepDirection, state.Sweep.Stacked ? "Vertical + Horizontal" : state.Sweep.Vertical ? "Vertical" : "Horizontal");
        }
    }

    public byte[] CopyBgra(DashboardPointerFrame pointer)
    {
        if (pointer.Position is not Point position)
        {
            CopyBgra(Frame, pixels);
            return pixels;
        }

        using (var g = Graphics.FromImage(vrFrame))
        {
            g.DrawImageUnscaled(Frame, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawHover(g, pointer.HoverBounds, pointer.HoverOpacity);
            using var halo = new Pen(Color.FromArgb(185, InterfaceStyle.Background), 3);
            using var ring = new Pen(InterfaceStyle.PointerPurple, 2);
            var cursor = new Rectangle(position.X - 7, position.Y - 7, 14, 14);
            g.DrawEllipse(halo, cursor);
            g.DrawEllipse(ring, cursor);
            g.FillEllipse(Brushes.White, position.X - 2, position.Y - 2, 4, 4);
        }
        CopyBgra(vrFrame, pixels);
        return pixels;
    }

    internal static void DrawHover(Graphics g, Rectangle target, byte opacity)
    {
        if (target.IsEmpty || opacity == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = Rectangle.Inflate(target, -1, -1);
        using var outline = InterfaceStyle.Rounded(bounds, 10);
        bool destructive = target == DeletePattern || target == ConfirmDelete;
        Color color = destructive ? Color.FromArgb(210, 36, 48) : Color.White;
        using var fill = new SolidBrush(Color.FromArgb(opacity * (destructive ? 210 : 24) / 220, color));
        g.FillPath(fill, outline);
        using var highlight = new Pen(Color.FromArgb(opacity * (destructive ? 220 : 85) / 220, color), 1);
        g.DrawPath(highlight, outline);
    }

    public static void CopyRgba(Bitmap bitmap, byte[] pixels)
    {
        CopyBgra(bitmap, pixels);
        SwapRedBlue(pixels);
    }

    private static void CopyBgra(Bitmap bitmap, byte[] pixels)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
    }

    internal static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    private static void NavigationItem(Graphics g, Rectangle bounds, string label, bool selected)
    {
        DrawButtonFace(g, bounds, Color.FromArgb(38, 38, 38), selected);
        using var font = new Font("Segoe UI", bounds.Height < 40 ? 17 : 21, selected ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(InterfaceStyle.Text);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(label, font, brush, new Rectangle(bounds.Left + 12, bounds.Top, bounds.Width - 18, bounds.Height), format);
    }

    private static Color OpaqueRgb(int rgb) => Color.FromArgb(unchecked((int)0xff000000) | rgb);

    private static void DrawCenterColorPicker(Graphics g, int rgb, int transparency, int size, bool scaleWithDistance)
    {
        Text(g, "Indicator color", new(286, 145, 500, 55), 32, InterfaceStyle.Text, bold: true);
        string[] labels = ["Red", "Green", "Blue"];
        for (int channel = 0; channel < labels.Length; channel++)
        {
            var bounds = ColorChannel(channel);
            int shift = (2 - channel) * 8;
            int mask = 255 << shift;
            int value = (rgb & mask) >> shift;
            int baseRgb = rgb & ~mask;
            Text(g, labels[channel], new(286, bounds.Y, 100, bounds.Height), 23, InterfaceStyle.Text);
            Text(g, value.ToString(), new(863, bounds.Y, 70, bounds.Height), 22, InterfaceStyle.Text);
            using var gradient = new LinearGradientBrush(bounds, OpaqueRgb(baseRgb), OpaqueRgb(baseRgb | mask), 0f);
            using var shape = InterfaceStyle.Rounded(bounds, 7);
            g.FillPath(gradient, shape);
            DrawColorSliderThumb(g, bounds, value / 255f);
        }
        var alphaBounds = ColorChannel(TransparencyChannel);
        Text(g, "Transparency", new(286, alphaBounds.Y, 178, alphaBounds.Height), 23, InterfaceStyle.Text);
        Text(g, $"{transparency}%", new(863, alphaBounds.Y, 70, alphaBounds.Height), 22, InterfaceStyle.Text);
        using var checker = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(115, 115, 115), Color.FromArgb(65, 65, 65));
        using var track = InterfaceStyle.Rounded(alphaBounds, 7);
        g.FillPath(checker, track);
        using var alphaGradient = new LinearGradientBrush(alphaBounds, OpaqueRgb(rgb), Color.FromArgb(0, OpaqueRgb(rgb)), 0f);
        g.FillPath(alphaGradient, track);
        DrawColorSliderThumb(g, alphaBounds, transparency / 100f);
        var sizeBounds = ColorChannel(SizeChannel);
        Text(g, "Size", new(286, sizeBounds.Y, 178, sizeBounds.Height), 23, InterfaceStyle.Text);
        Text(g, $"{size}%", new(863, sizeBounds.Y, 85, sizeBounds.Height), 22, InterfaceStyle.Text);
        InterfaceStyle.FillRounded(g, sizeBounds, 7, InterfaceStyle.Button);
        DrawColorSliderThumb(g, sizeBounds, (size - MinCenterSize) / (float)(MaxCenterSize - MinCenterSize));

        using var preview = InterfaceStyle.Rounded(new Rectangle(286, 555, 70, 48), 8);
        g.FillPath(checker, preview);
        using var previewColor = new SolidBrush(Color.FromArgb((100 - transparency) * 255 / 100, OpaqueRgb(rgb)));
        g.FillPath(previewColor, preview);
        Text(g, $"#{rgb:X6}", new(374, 555, 180, 48), 24, InterfaceStyle.Text);
        Text(g, "Scale with distance", new(502, 555, 232, 48), 22, InterfaceStyle.Text);
        Button(g, ScaleCenterWithDistance, scaleWithDistance ? "On" : "Off", scaleWithDistance);
        Button(g, CloseColorPicker, "Done");
    }

    private static void DrawColorSliderThumb(Graphics g, Rectangle bounds, float fraction)
    {
        float x = bounds.Left + fraction * (bounds.Width - 1);
        using var edge = new Pen(Color.Black, 5);
        using var highlight = new Pen(Color.White, 2);
        g.DrawLine(edge, x, bounds.Top - 2, x, bounds.Bottom + 2);
        g.DrawLine(highlight, x, bounds.Top - 2, x, bounds.Bottom + 2);
    }

    private static void Button(Graphics g, Rectangle bounds, string label, bool selected = false, bool available = true)
    {
        DrawButtonFace(g, bounds, available ? InterfaceStyle.Button : InterfaceStyle.Card, selected);
        Text(g, label, Rectangle.Inflate(bounds, -6, -2), 22, available ? InterfaceStyle.Text : InterfaceStyle.Muted, centered: true);
    }

    private static void DrawButtonFace(Graphics g, Rectangle bounds, Color face, bool selected)
    {
        InterfaceStyle.FillRounded(g, bounds, 9, face);
        if (!selected) return;
        Rectangle outline = Rectangle.Inflate(bounds, -1, -1);
        using var path = InterfaceStyle.Rounded(outline, 8);
        using var pen = new Pen(InterfaceStyle.Purple, 2);
        g.DrawPath(pen, path);
    }
    private static void Text(Graphics g, string value, Rectangle bounds, int size, Color color,
        bool centered = false, bool bold = false)
    {
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = centered ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = centered ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter
        };
        if (!value.Contains('\n')) format.FormatFlags |= StringFormatFlags.NoWrap;
        for (int fontSize = size; ; fontSize--)
        {
            using var font = new Font("Segoe UI", fontSize, style, GraphicsUnit.Pixel);
            SizeF measured = g.MeasureString(value, font, new SizeF(bounds.Width, 10000), format,
                out int fitted, out _);
            if (fontSize <= Math.Min(size, 12) ||
                (fitted == value.Length && measured.Width <= bounds.Width && measured.Height <= bounds.Height))
            {
                g.DrawString(value, font, brush, bounds, format);
                return;
            }
        }
    }

    public void SavePreview(string path) => Frame.Save(path, ImageFormat.Png);

    public void Dispose()
    {
        vrFrame.Dispose();
        Frame.Dispose();
        logo.Dispose();
    }
}
