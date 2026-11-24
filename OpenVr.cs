using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

internal sealed unsafe class OpenVr : IDisposable
{
    public const int Standing = 1;
    private const string AppKey = "strik3.ovr_space_control";
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "SteamVR");
    private readonly nint system, input, overlay, chaperone, setup, applications;
    private ActionSet actionSet, turnSet;
    private ulong turnAction, turnTeleportAction, leftHandSource, rightHandSource;
    private bool turnSetActive;
    private ulong teleportAction, resetAction, aimAction, marker, laser, circleGuide;
    private ulong dashboard, dashboardIcon;
    private DashboardTexture? dashboardTexture;
    private byte[]? previousDashboardPixels;
    private bool initialized, markerVisible, laserVisible, circleGuideVisible;
    private (int CirclePoints, int Rows, int PointsPerRow, bool Random, decimal GapRatio, bool Stacked) guideLayout;
    private int guideRevision;
    private byte[]? upperGuideTexture, reversedGuideTexture;
    private readonly (ulong Handle, bool Visible, int Revision, float Width)[] upperGuides = new (ulong, bool, int, float)[SweepControls.MaxRows - 1];
    private float guideWidth;
    private bool? previewValid;
    private Color? flightLineColor;

    static OpenVr()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenVr).Assembly, (name, _, _) =>
        {
            if (name != "openvr_api") return 0;
            using var server = FindServer();
            var serverPath = server?.MainModule?.FileName;
            var directory = serverPath is null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Steam", "steamapps", "common", "SteamVR", "bin", "win64")
                : Path.GetDirectoryName(serverPath)!;
            return NativeLibrary.Load(Path.Combine(directory, "openvr_api.dll"));
        });
    }

    private static Process? FindServer()
    {
        var servers = Process.GetProcessesByName("vrserver");
        foreach (var extra in servers.Skip(1)) extra.Dispose();
        return servers.FirstOrDefault();
    }

    public OpenVr()
    {
        using var server = FindServer();
        if (server is null) throw new InvalidOperationException("Waiting for SteamVR.");
        if (!WaitNamedPipe(@"\\.\pipe\SteamVR_Namespace", 2000))
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Waiting for SteamVR to respond (Windows error {errorCode}).");
        }

        VR_InitInternal(out var error, 2); // VRApplication_Overlay
        if (error != 0)
            throw new InvalidOperationException($"SteamVR connection failed ({error}). {Marshal.PtrToStringAnsi(VR_GetVRInitErrorAsEnglishDescription(error))}");
        initialized = true;
        try
        {
            system = Interface("IVRSystem_026");
            overlay = Interface("IVROverlay_028");
            input = Interface("IVRInput_011");
            chaperone = Interface("IVRChaperone_004");
            setup = Interface("IVRChaperoneSetup_006");
            applications = Interface("IVRApplications_008");

            RegisterInput();
            CreateMarker();
            CreateLaser();
            circleGuide = CreateAimOverlay(".pattern", "Pattern landing points");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool GetAutoLaunch()
    {
        bool enabled = false;
        UseString(AppKey, key => enabled = ((delegate* unmanaged<byte*, byte>)Slot(applications, 18))(key) != 0);
        return enabled;
    }

    public void SetAutoLaunch(bool enabled) => UseString(AppKey, key =>
        Check(((delegate* unmanaged<byte*, byte, int>)Slot(applications, 17))(key, enabled ? (byte)1 : (byte)0),
            "Set SteamVR auto-launch"));

    private void RegisterInput()
    {
        UseString(Path.Combine(Assets, "app.vrmanifest"), text =>
            Check(((delegate* unmanaged<byte*, byte, int>)Slot(applications, 0))(text, 0), "Register SteamVR app"));
        UseString(AppKey, text =>
            Check(((delegate* unmanaged<uint, byte*, int>)Slot(applications, 11))((uint)Environment.ProcessId, text), "Identify SteamVR app"));
        UseString(Path.Combine(Assets, "actions.json"), text =>
            Check(((delegate* unmanaged<byte*, int>)Slot(input, 0))(text), "Load controller actions"));
        actionSet.Handle = ReadHandle(1, "/actions/teleport");
        teleportAction = ReadHandle(2, "/actions/teleport/in/teleport");
        resetAction = ReadHandle(2, "/actions/teleport/in/reset");
        aimAction = ReadHandle(2, "/actions/teleport/in/aim");
        leftHandSource = ReadHandle(3, "/user/hand/left");
        rightHandSource = ReadHandle(3, "/user/hand/right");
        turnSet.Handle = ReadHandle(1, "/actions/turn");
        turnSet.Priority = 0x01000000; // Valve's minimum overlay-global input priority.
        turnAction = ReadHandle(2, "/actions/turn/in/stick");
        turnTeleportAction = ReadHandle(2, "/actions/turn/in/teleport");
    }

    private ulong ReadHandle(int slot, string path)
    {
        ulong result = 0;
        UseString(path, text =>
        {
            ulong handle = 0;
            Check(((delegate* unmanaged<byte*, ulong*, int>)Slot(input, slot))(text, &handle), "Load " + path);
            result = handle;
        });
        return result;
    }

    public void OpenBindings() => UseString(AppKey, text =>
        Check(((delegate* unmanaged<byte*, ulong, ulong, byte, int>)Slot(input, 32))(text, actionSet.Handle, 0, 1),
            "Open SteamVR bindings"));

    private void CreateMarker()
    {
        marker = CreateAimOverlay(".floor", "Teleport destination");
        const int side = 128;
        var pixels = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            float radius = Vector2.Distance(new(x, y), new(63.5f));
            int index = (y * side + x) * 4;
            float ring = Math.Clamp(3.8f - MathF.Abs(radius - 56), 0, 1);
            float dot = Math.Clamp(4f - radius, 0, 1);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
            pixels[index + 3] = (byte)(Math.Max(ring, dot) * 255);
        }
        UploadAimTexture(marker, pixels, side, side);
        Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(marker, .42f), "Size floor marker");
    }

    private const int LaserWidth = 256, LaserHeight = 4;

    private void CreateLaser()
    {
        laser = CreateAimOverlay(".laser", "Teleport laser");
        var pixels = new byte[LaserWidth * LaserHeight * 4];
        for (int y = 0; y < LaserHeight; y++)
        for (int x = 0; x < LaserWidth; x++)
        {
            int index = (y * LaserWidth + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
            pixels[index + 3] = y is 0 or LaserHeight - 1 ? (byte)72 : (byte)240;
        }
        UploadAimTexture(laser, pixels, LaserWidth, LaserHeight);
    }

    private ulong CreateAimOverlay(string suffix, string label)
    {
        ulong handle = 0;
        UseString(AppKey + suffix, key => UseString(label, name =>
        {
            ulong result = 0;
            Check(((delegate* unmanaged<byte*, byte*, ulong*, int>)Slot(overlay, 1))(key, name, &result), "Create " + label);
            handle = result;
        }));
        return handle;
    }

    private void UploadAimTexture(ulong handle, byte[] pixels, uint width, uint height)
    {
        fixed (byte* data = pixels)
            Check(((delegate* unmanaged<ulong, byte*, uint, uint, uint, int>)Slot(overlay, 62))
                (handle, data, width, height, 4), "Upload aiming texture");
    }

    private ulong customGuide;
    private bool customGuideVisible;
    private bool customGuideAimed;
    private Vector3[] guidePoints = [];
    private Vector3 guideCenter;

    public void ShowCustomGuide(IReadOnlyList<Vector3>? points, Vector3? start, bool clearAimOnly = false)
    {
        if (points is null || points.Count == 0)
        {
            if (customGuideVisible && (!clearAimOnly || !customGuideAimed))
                SetAimVisible(customGuide, ref customGuideVisible, false);
            return;
        }

        bool aimed = start.HasValue;
        if (aimed != customGuideAimed || !guidePoints.SequenceEqual(points))
        {
            var locations = points.Select(point => point - points[0]).ToArray();
            float minX = locations.Min(point => point.X), maxX = locations.Max(point => point.X);
            float minZ = locations.Min(point => point.Z), maxZ = locations.Max(point => point.Z);
            float span = Math.Max(1f, Math.Max(maxX - minX, maxZ - minZ) * 1.08f + .4f);
            if (!float.IsFinite(span))
            {
                ShowCustomGuide(null, null);
                return;
            }
            guideCenter = new((minX + maxX) * .5f, 0, (minZ + maxZ) * .5f);
            const int size = 2048;
            using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float pixelsPerMeter = size / span;
                float arrow = .16f * pixelsPerMeter;
                using var path = new Pen(PatternGuideStyle.Line, .025f * pixelsPerMeter);
                using var pointBrush = new SolidBrush(PatternGuideStyle.Point);
                using var ring = new Pen(PatternGuideStyle.Point, .035f * pixelsPerMeter);
                var pixels = locations.Select(point => new Vector2(
                    size * (.5f + (point.X - guideCenter.X) / span),
                    size * (.5f + (point.Z - guideCenter.Z) / span))).ToArray();
                for (int index = 1; index < pixels.Length; index++)
                    PatternGuideStyle.DrawSegment(graphics, path, pixels[index - 1], pixels[index], arrow);
                for (int index = pixels.Length - 1; index >= 0; index--)
                {
                    float radius = (aimed ? (index == 0 ? .10f : .075f) : .16f) * pixelsPerMeter;
                    var p = pixels[index];
                    if (aimed) graphics.FillEllipse(pointBrush, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                    else graphics.DrawEllipse(ring, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                }
            }

            if (customGuide == 0) customGuide = CreateAimOverlay(".custom-route", "Custom pattern route");
            var bits = bitmap.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);
            var data = new byte[size * size * 4];
            try { Marshal.Copy(bits.Scan0, data, 0, data.Length); }
            finally { bitmap.UnlockBits(bits); }
            DashboardPanel.SwapRedBlue(data);
            UploadAimTexture(customGuide, data, size, size);
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(customGuide, span), "Size custom guide");
            guidePoints = points.ToArray();
            customGuideAimed = aimed;
        }

        Vector3 center = (start ?? points[0]) + guideCenter;
        SetAimPose(customGuide, new Matrix34(1, 0, 0, center.X, 0, 0, 1, .022f, 0, -1, 0, center.Z));
        SetAimVisible(customGuide, ref customGuideVisible, true);
    }
    private const float BoundaryGridWidth = 16f;
    private ulong ceilingGrid, ceilingGridBack;
    private bool ceilingGridVisible, ceilingGridBackVisible;
    private float ceilingGridHeight = float.NaN;

    private readonly ulong[] wallGrids = new ulong[5];
    private readonly bool[] wallGridVisible = new bool[5];
    private readonly ulong[] wallGridBack = new ulong[5];
    private readonly bool[] wallGridBackVisible = new bool[5];

    public void ShowWalls(IReadOnlyList<DragWall> walls, float opacity, DragWall? preview = null)
    {
        for (int i = 0; i < wallGrids.Length; i++)
        {
            bool previewSlot = i == wallGrids.Length - 1;
            DragWall? grid = previewSlot ? preview : i < walls.Count ? walls[i] : null;
            if (opacity <= 0 || grid is not DragWall wall)
            {
                if (wallGrids[i] != 0) SetAimVisible(wallGrids[i], ref wallGridVisible[i], false);
                if (wallGridBack[i] != 0) SetAimVisible(wallGridBack[i], ref wallGridBackVisible[i], false);
                continue;
            }
            if (wallGrids[i] == 0)
            {
                wallGrids[i] = CreateAimOverlay($".wall-{i}", $"Wall {i + 1}");
                UploadGrid(wallGrids[i]);
                wallGridBack[i] = CreateAimOverlay($".wall-back-{i}", $"Wall {i + 1} reverse");
                UploadGrid(wallGridBack[i]);
            }
            Vector3 right = Vector3.Cross(Vector3.UnitY, wall.Normal);
            Vector3 center = wall.Center with { Y = BoundaryGridWidth / 2 };
            SetAimPose(wallGrids[i], Matrix34.FromBasis(right, Vector3.UnitY, wall.Normal, center));
            SetAimPose(wallGridBack[i], Matrix34.FromBasis(-right, Vector3.UnitY, -wall.Normal, center));
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 16))(wallGrids[i], previewSlot ? opacity * .55f : opacity), "Fade wall grid");
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 16))(wallGridBack[i], previewSlot ? opacity * .55f : opacity), "Fade reverse wall grid");
            SetAimVisible(wallGrids[i], ref wallGridVisible[i], true);
            SetAimVisible(wallGridBack[i], ref wallGridBackVisible[i], true);
        }
    }

    private void UploadGrid(ulong handle)
    {
        const int size = 1024;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (x % 32 >= 2 && y % 32 >= 2 && x != size - 1 && y != size - 1) continue;
            int index = (y * size + x) * 4;
            pixels[index] = 130;
            pixels[index + 1] = 210;
            pixels[index + 2] = 255;
            pixels[index + 3] = 210;
        }
        UploadAimTexture(handle, pixels, size, size);
        Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(handle, BoundaryGridWidth), "Size guide grid");
    }

    private ulong centerMarker;
    private bool centerMarkerVisible;

    private int centerMarkerColor = -1;
    private int centerMarkerTransparency = -1;
    private float centerMarkerWidth;
    private float centerMarkerDistanceScale = 1f;
    private static readonly Vector3 CenterMarkerPosition = new(0, .01f, 0);

    public void ShowPlaySpaceCenter(bool show, int rgb, int transparency, int sizePercent, bool scaleWithDistance)
    {
        if (!show)
        {
            if (centerMarker != 0) SetAimVisible(centerMarker, ref centerMarkerVisible, false);
            return;
        }

        if (centerMarker == 0)
        {
            centerMarker = CreateAimOverlay(".play-space-center", "Play space center");
            const int size = 128;
            var pixels = new byte[size * size * 4];
            const float radius = 8f;
            const float halfLength = 39f;
            const float diagonal = .70710678f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + y - size + 1) * diagonal;
                float v = (x - y) * diagonal;
                float pastU = Math.Max(0, Math.Abs(u) - halfLength);
                float pastV = Math.Max(0, Math.Abs(v) - halfLength);
                float first = MathF.Sqrt(v * v + pastU * pastU);
                float second = MathF.Sqrt(u * u + pastV * pastV);
                // Blend the strokes where they meet to round the inside corners.
                const float joinRadius = 4f;
                float overlap = Math.Max(0, joinRadius - Math.Abs(first - second));
                float distance = Math.Min(first, second) - overlap * overlap / (4 * joinRadius);
                const float ringRadius = 56f;
                const float ringHalfWidth = 3.5f;
                float ringDistance = Math.Abs(MathF.Sqrt(u * u + v * v) - ringRadius);
                float edgeDistance = Math.Min(distance - radius, ringDistance - ringHalfWidth);
                int index = (y * size + x) * 4;
                byte shade = (byte)(255 * Math.Clamp(-edgeDistance - 1f, 0f, 1f));
                pixels[index] = pixels[index + 1] = pixels[index + 2] = shade;
                pixels[index + 3] = (byte)(255 * Math.Clamp(.5f - edgeDistance, 0f, 1f));
            }
            UploadAimTexture(centerMarker, pixels, size, size);
        }
        if (!centerMarkerVisible)
        {
            // Normal Space Drag moves the headset through the standing coordinate space.
            var pose = Matrix34.FromBasis(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY, CenterMarkerPosition);
            SetAimPose(centerMarker, pose);
            centerMarkerDistanceScale = 1f;
        }

        if (scaleWithDistance)
        {
            var head = HeadPose(Standing);
            float distance = Vector3.Distance(head.Matrix.Position, CenterMarkerPosition);
            // The size slider sets the width at a viewing distance of two meters.
            if (head.Tracked && float.IsFinite(distance))
                centerMarkerDistanceScale = Math.Clamp(MathF.Sqrt(distance / 2f), .85f, 1.75f);
        }
        else centerMarkerDistanceScale = 1f;

        float width = .2f * sizePercent / 100f * centerMarkerDistanceScale;
        if (Math.Abs(width - centerMarkerWidth) > width * .0025f)
        {
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))
                (centerMarker, width), "Size play space center");
            centerMarkerWidth = width;
        }
        if (centerMarkerColor != rgb)
        {
            Tint(centerMarker, Color.FromArgb(unchecked((int)0xff000000) | rgb));
            centerMarkerColor = rgb;
        }
        if (centerMarkerTransparency != transparency)
        {
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 16))
                (centerMarker, 1f - transparency / 100f), "Set play space center opacity");
            centerMarkerTransparency = transparency;
        }
        SetAimVisible(centerMarker, ref centerMarkerVisible, true);
    }

    private ulong floorGrid;
    private bool floorGridVisible;
    private float floorGridHeight;

    public void ShowFloor(float? height)
    {
        if (height is not float floor)
        {
            if (floorGrid != 0) SetAimVisible(floorGrid, ref floorGridVisible, false);
            return;
        }

        if (floorGrid == 0)
        {
            floorGrid = CreateAimOverlay(".boundary-floor", "Floor boundary");
            UploadGrid(floorGrid);
        }
        if (!floorGridVisible || floor != floorGridHeight)
        {
            var (head, _) = Poses(0);
            Vector3 center = head.Tracked ? head.Matrix.Position : Vector3.Zero;
            SetAimPose(floorGrid, new Matrix34(1, 0, 0, center.X, 0, 0, 1, floor + .015f, 0, -1, 0, center.Z));
            floorGridHeight = floor;
        }
        SetAimVisible(floorGrid, ref floorGridVisible, true);
    }
    public void ShowCeiling(float height, float opacity)
    {
        if (opacity <= 0)
        {
            if (ceilingGrid != 0) SetAimVisible(ceilingGrid, ref ceilingGridVisible, false);
            if (ceilingGridBack != 0) SetAimVisible(ceilingGridBack, ref ceilingGridBackVisible, false);
            return;
        }

        if (ceilingGrid == 0)
        {
            ceilingGrid = CreateAimOverlay(".ceiling", "Ceiling height preview");
            UploadGrid(ceilingGrid);
            ceilingGridBack = CreateAimOverlay(".ceiling-back", "Ceiling reverse");
            UploadGrid(ceilingGridBack);
        }

        if (!ceilingGridVisible || height != ceilingGridHeight)
        {
            var (head, _) = Poses(0);
            Vector3 center = head.Tracked ? head.Matrix.Position : Vector3.Zero;
            SetAimPose(ceilingGrid, new Matrix34(1, 0, 0, center.X, 0, 0, -1, height, 0, 1, 0, center.Z));
            SetAimPose(ceilingGridBack, new Matrix34(1, 0, 0, center.X, 0, 0, 1, height, 0, -1, 0, center.Z));
            ceilingGridHeight = height;
        }
        Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 16))(ceilingGrid, opacity), "Fade ceiling grid");
        Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 16))(ceilingGridBack, opacity), "Fade reverse ceiling grid");
        SetAimVisible(ceilingGrid, ref ceilingGridVisible, true);
        SetAimVisible(ceilingGridBack, ref ceilingGridBackVisible, true);
    }
    public void Preview(AimPreview? aim, Vector3 eye = default, CircleControls? circle = null, SweepControls? sweep = null)
    {
        if (aim is AimPreview current && previewValid != current.CanTeleport)
        {
            Tint(marker, current.CanTeleport ? PatternGuideStyle.Point : InterfaceStyle.PointerRed);
            Tint(laser, current.CanTeleport ? PatternGuideStyle.Line : InterfaceStyle.PointerRed);
            previewValid = current.CanTeleport;
            flightLineColor = null;
        }

        if (aim?.FloorPoint is Vector3 target)
        {
            // The overlay faces upward, with its center just above the tracking floor.
            var pose = new Matrix34(1, 0, 0, target.X, 0, 0, 1, .015f, 0, -1, 0, target.Z);
            SetAimPose(marker, pose);
        }
        SetAimVisible(marker, ref markerVisible, aim?.FloorPoint is not null);

        ShowDirectionLine(aim, eye);
        if (aim is { CanTeleport: true, FloorPoint: Vector3 center } && (circle.HasValue || sweep.HasValue))
        {
            var layout = (CirclePoints: circle?.Points ?? 0, Rows: sweep?.Rows ?? 0,
                PointsPerRow: sweep?.PointsPerRow ?? 0, Random: sweep?.Random ?? circle?.Random ?? false, GapRatio: sweep is { } spacing ? spacing.EffectiveRowDistance / spacing.Width : 0, Stacked: sweep?.Stacked ?? false);
            if (guideLayout != layout)
            {
                byte[] texture = sweep is { } sweepStyle ? SweepGuide.MakeTexture(sweepStyle) : CircleGuide.MakeTexture(layout.CirclePoints, layout.Random);
                UploadAimTexture(circleGuide, texture, CircleGuide.Size, CircleGuide.Size);
                guideLayout = layout;
                guideRevision++;
                upperGuideTexture = reversedGuideTexture = null;
            }
            float width = sweep is { } rows
                ? SweepGuide.OverlayWidth(rows)
                : (float)circle!.Value.Radius / CircleGuide.RadiusFraction;
            if (guideWidth != width)
            {
                Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))
                    (circleGuide, width), "Size pattern guide");
                guideWidth = width;
            }
            var pose = sweep is { } sweepPose ? SweepGuide.Pose(center, eye, sweepPose)
                : CircleGuide.Pose(center, FloorPattern.StartAngle(center, eye));
            SetAimPose(circleGuide, pose);
            SetAimVisible(circleGuide, ref circleGuideVisible, true);
            PreviewUpperLayers(sweep, pose, width);
        }
        else
        {
            SetAimVisible(circleGuide, ref circleGuideVisible, false);
            PreviewUpperLayers(null, default, 0);
        }
    }

    private void PreviewUpperLayers(SweepControls? sweep, Matrix34 pose, float width)
    {
        int upperCount = (sweep?.Layers ?? 1) - 1;
        for (int i = 0; i < upperGuides.Length; i++)
        {
            ref var layer = ref upperGuides[i];
            if (i >= upperCount)
            {
                if (layer.Handle != 0) SetAimVisible(layer.Handle, ref layer.Visible, false);
                continue;
            }
            var settings = sweep!.Value;
            if (layer.Handle == 0)
                layer.Handle = CreateAimOverlay($".pattern-layer-{i + 1}", $"Sweep layer {i + 2}");
            if (layer.Revision != guideRevision)
            {
                byte[] texture = i % 2 == 0
                    ? (reversedGuideTexture ??= SweepGuide.MakeTexture(settings, markStart: false, reverse: true))
                    : (upperGuideTexture ??= SweepGuide.MakeTexture(settings, markStart: false));
                UploadAimTexture(layer.Handle, texture, CircleGuide.Size, CircleGuide.Size);
                layer.Revision = guideRevision;
            }
            if (layer.Width != width)
            {
                Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(layer.Handle, width), "Size sweep layer");
                layer.Width = width;
            }
            SetAimPose(layer.Handle, pose.Translate(SweepGuide.LayerOffset(settings, i + 1)));
            SetAimVisible(layer.Handle, ref layer.Visible, true);
        }
    }

    public void ShowDirectionLine(AimPreview? aim, Vector3 eye = default, Color? flightColor = null)
    {
        var beam = aim?.Laser(eye);
        if (beam is { } line)
        {
            if (flightColor is Color color && flightLineColor != color)
            {
                Tint(laser, color);
                flightLineColor = color;
                previewValid = null;
            }
            float thickness = flightColor.HasValue ? .006f : .009f;
            float texelAspect = line.Length / (thickness * LaserWidth / LaserHeight);
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 18))(laser, texelAspect), "Set laser aspect ratio");
            Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(laser, line.Length), "Size laser");
            SetAimPose(laser, line.Pose);
        }
        SetAimVisible(laser, ref laserVisible, beam.HasValue);
    }

    private void SetAimPose(ulong handle, Matrix34 pose) =>
        Check(((delegate* unmanaged<ulong, int, Matrix34*, int>)Slot(overlay, 33))(handle, Standing, &pose), "Position aim overlay");

    private void Tint(ulong handle, Color color) =>
        Check(((delegate* unmanaged<ulong, float, float, float, int>)Slot(overlay, 14))
            (handle, color.R / 255f, color.G / 255f, color.B / 255f), "Color aim overlay");

    private void SetAimVisible(ulong handle, ref bool visible, bool show)
    {
        if (show == visible) return;
        Check(((delegate* unmanaged<ulong, int>)Slot(overlay, show ? 43 : 44))(handle), "Set aim overlay visibility");
        visible = show;
    }

    public bool QuitRequested()
    {
        // The Windows VREvent_t has a 16-byte header and a 48-byte data union.
        uint* data = stackalloc uint[16];
        while (((delegate* unmanaged<uint*, uint, byte>)Slot(system, 30))(data, 64) != 0)
            if (data[0] == 700) return true;
        return false;
    }

    public int Calibration => ((delegate* unmanaged<int>)Slot(chaperone, 0))();
    public bool DashboardVisible => ((delegate* unmanaged<byte>)Slot(overlay, 68))() != 0;
    public bool DashboardActive => dashboard != 0 &&
        ((delegate* unmanaged<ulong, byte>)Slot(overlay, 69))(dashboard) != 0;

    public void CreateDashboard()
    {
        UseString(AppKey, key => UseString("OVR Space Control", name =>
        {
            ulong panel = 0, icon = 0;
            Check(((delegate* unmanaged<byte*, byte*, ulong*, ulong*, int>)Slot(overlay, 67))
                (key, name, &panel, &icon), "Create SteamVR dashboard");
            dashboard = panel;
            dashboardIcon = icon;
        }));
        Check(((delegate* unmanaged<ulong, int, int>)Slot(overlay, 50))(dashboard, 1), "Enable dashboard controller input");
        var scale = new Vector2(DashboardPanel.Width, DashboardPanel.Height);
        Check(((delegate* unmanaged<ulong, Vector2*, int>)Slot(overlay, 52))(dashboard, &scale), "Set dashboard input scale");
        Check(((delegate* unmanaged<ulong, float, int>)Slot(overlay, 22))(dashboard, 1.6f), "Size dashboard");
        UseString(Path.Combine(Assets, "dashboard-icon.png"), path =>
            Check(((delegate* unmanaged<ulong, byte*, int>)Slot(overlay, 63))(dashboardIcon, path), "Set dashboard icon"));
        int adapter = -1;
        ((delegate* unmanaged<int*, void>)Slot(system, 8))(&adapter); // GetDXGIOutputInfo
        dashboardTexture = DashboardTexture.TryCreate(adapter);
        if (dashboardTexture is not null)
            ((delegate* unmanaged<ulong, int, int>)Slot(overlay, 28))(dashboard, 1); // Gamma color space
    }

    public void OpenDashboard() => UseString(AppKey, key =>
        ((delegate* unmanaged<byte*, void>)Slot(overlay, 72))(key));

    public void UploadDashboard(byte[] bgra)
    {
        if (previousDashboardPixels is not null && bgra.AsSpan().SequenceEqual(previousDashboardPixels)) return;
        if (!TrySubmitDashboardTexture(bgra))
        {
            DashboardPanel.SwapRedBlue(bgra);
            try
            {
                fixed (byte* pixels = bgra)
                    Check(((delegate* unmanaged<ulong, byte*, uint, uint, uint, int>)Slot(overlay, 62))
                        (dashboard, pixels, DashboardPanel.Width, DashboardPanel.Height, 4), "Draw SteamVR dashboard");
            }
            finally { DashboardPanel.SwapRedBlue(bgra); }
        }
        previousDashboardPixels ??= new byte[bgra.Length];
        bgra.CopyTo(previousDashboardPixels, 0);
    }

    private bool TrySubmitDashboardTexture(byte[] bgra)
    {
        if (dashboardTexture is not { } texture) return false;
        var native = new OverlayTexture(texture.Upload(bgra), 0, 1); // DirectX, gamma-encoded BGRA
        if (native.Handle != 0 &&
            ((delegate* unmanaged<ulong, OverlayTexture*, int>)Slot(overlay, 60))(dashboard, &native) == 0)
            return true;
        Trace.WriteLine("SteamVR shared dashboard texture submission failed. Falling back to raw pixels.");
        texture.Dispose();
        dashboardTexture = null;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OverlayTexture(nint Handle, int Type, int ColorSpace);

    private ulong nameKeyboardToken;
    public bool NameKeyboardOpen { get; private set; }

    public int ShowNameKeyboard(string name, string title = "Pattern name")
    {
        if (NameKeyboardOpen) return 0;
        int error = 0;
        ulong token = ++nameKeyboardToken;
        UseString(title, description => UseString(name, existing =>
        {
            const uint modal = 1 << 1;
            error = ((delegate* unmanaged<ulong, int, int, uint, byte*, uint, byte*, ulong, int>)Slot(overlay, 75))
                (dashboard, 0, 0, modal, description, 32, existing, token);
        }));
        NameKeyboardOpen = error == 0;
        return error;
    }

    public void HideNameKeyboard()
    {
        if (!NameKeyboardOpen) return;
        NameKeyboardOpen = false;
        ((delegate* unmanaged<void>)Slot(overlay, 77))();
    }
    public bool TryReadDashboardPointer(out PointF? point, out bool? down, out string? name, out bool keyboardClosed)
    {
        name = null;
        keyboardClosed = false;
        uint* data = stackalloc uint[16];
        while (((delegate* unmanaged<ulong, uint*, uint, byte>)Slot(overlay, 48))(dashboard, data, 64) != 0)
        {
            if (data[0] is 1200 or 1202)
            {
                // VREvent_Keyboard_t begins at byte 16 on Windows, with the token at byte 24.
                if (!NameKeyboardOpen || *(ulong*)(data + 6) != nameKeyboardToken) continue;
                NameKeyboardOpen = false;
                keyboardClosed = true;
                if (data[0] == 1202)
                {
                    Span<byte> text = stackalloc byte[129];
                    text.Clear();
                    fixed (byte* buffer = text)
                        ((delegate* unmanaged<byte*, uint, uint>)Slot(overlay, 76))(buffer, (uint)text.Length);
                    int length = text.IndexOf((byte)0);
                    name = System.Text.Encoding.UTF8.GetString(text[..(length < 0 ? text.Length : length)]);
                }
                point = null;
                down = null;
                return true;
            }
            if (NameKeyboardOpen) continue;
            if (data[0] is 304 or 501) // Focus leave or overlay hidden.
            {
                point = null;
                down = null;
                return true;
            }
            if (data[0] is not (300 or 301 or 302) || (data[0] != 300 && data[6] != 1)) continue;
            point = new PointF(*(float*)&data[4], DashboardPanel.Height - *(float*)&data[5]);
            down = data[0] == 300 ? null : data[0] == 301;
            return true;
        }
        point = default;
        down = null;
        return false;
    }

    public (DigitalAction Teleport, DigitalAction Reset, float Stick, bool StickBound) ReadControls(bool turn = false)
    {
        UpdateActionSets(turn);
        AnalogAction axis = default;
        DigitalAction action = default;
        if (turn)
        {
            Check(((delegate* unmanaged<ulong, AnalogAction*, uint, ulong, int>)Slot(input, 6))
                (turnAction, &axis, (uint)sizeof(AnalogAction), 0), "Read right stick");
            action = ReadDigitalAction(turnTeleportAction, "Read Space Turn teleport button");
        }
        if (action.Active == 0) action = ReadDigitalAction(teleportAction, "Read teleport button");
        return (action, ReadDigitalAction(resetAction, "Read reset button"), axis.X, axis.Active != 0);
    }


    public void ReleaseTurn()
    {
        if (turnSetActive) UpdateActionSets(false);
    }

    private void UpdateActionSets(bool turn)
    {
        ActionSet* sets = stackalloc ActionSet[2];
        sets[0] = actionSet;
        if (turn) sets[1] = turnSet;
        Check(((delegate* unmanaged<ActionSet*, uint, uint, int>)Slot(input, 4))
            (sets, (uint)sizeof(ActionSet), turn ? 2u : 1u), "Read controller actions");
        turnSetActive = turn;
    }

    private DigitalAction ReadDigitalAction(ulong handle, string operation)
    {
        DigitalAction action = default;
        Check(((delegate* unmanaged<ulong, DigitalAction*, uint, ulong, int>)Slot(input, 5))
            (handle, &action, (uint)sizeof(DigitalAction), 0), operation);
        return action;
    }

    public uint ControllerIndex(ulong origin)
    {
        OriginInfo info = default;
        if (origin == 0 || ((delegate* unmanaged<ulong, OriginInfo*, uint, int>)Slot(input, 26))
            (origin, &info, (uint)sizeof(OriginInfo)) != 0 || info.Path == 0) return uint.MaxValue;
        return info.Device;
    }

    public (TrackedPose Head, TrackedPose Controller) Poses(ulong origin, bool raw = false, bool allowAnyAimSource = false)
    {
        OriginInfo info = default;
        if (origin != 0 && ((delegate* unmanaged<ulong, OriginInfo*, uint, int>)Slot(input, 26))
            (origin, &info, (uint)sizeof(OriginInfo)) != 0)
            info = default;
        var device = info.Device;
        TrackedPose* poses = stackalloc TrackedPose[64];
        ((delegate* unmanaged<int, float, TrackedPose*, uint, void>)Slot(system, 12))(raw ? 2 : Standing, 0, poses, 64);
        TrackedPose controller = (info.Path != 0 && device is > 0 and < 64) ? poses[device] : default;
        if (!raw && (info.Path != 0 || allowAnyAimSource))
        {
            var aim = AimPose(Standing, info.Path);
            if (aim.Tracked) controller = aim;
        }
        return (poses[0], controller);
    }

    public TrackedPose FlightDirectionPose(DragPivot direction) => direction switch
    {
        DragPivot.LeftController => AimPose(2, leftHandSource),
        DragPivot.RightController => AimPose(2, rightHandSource),
        _ => RawDevicePose(0)
    };

    private TrackedPose AimPose(int trackingOrigin, ulong source)
    {
        PoseAction aim = default;
        int result = ((delegate* unmanaged<ulong, int, float, PoseAction*, uint, ulong, int>)Slot(input, 7))
            (aimAction, trackingOrigin, 0, &aim, (uint)sizeof(PoseAction), source);
        return result == 0 && aim.Active != 0 ? aim.Pose : default;
    }

    public uint RotationPivotIndex(DragPivot pivot)
    {
        if (pivot == DragPivot.Headset) return 0;
        if (pivot < DragPivot.FirstTracker)
            return ((delegate* unmanaged<int, uint>)Slot(system, 18))(pivot == DragPivot.LeftController ? 1 : 2);

        int ordinal = (int)(pivot - DragPivot.FirstTracker);
        foreach (uint tracker in ConnectedTrackers())
            if (ordinal-- == 0) return tracker;
        return 0;
    }

    public uint[] ConnectedTrackers()
    {
        var deviceClass = (delegate* unmanaged<uint, int>)Slot(system, 19);
        var connected = (delegate* unmanaged<uint, byte>)Slot(system, 20);
        var trackers = new List<uint>();
        for (uint index = 1; index < 64; index++)
            if (deviceClass(index) == 3 && connected(index) != 0) trackers.Add(index);
        return trackers.ToArray();
    }

    public TrackedPose RawDevicePose(uint index)
    {
        if (index >= 64) return default;
        TrackedPose* poses = stackalloc TrackedPose[64];
        ((delegate* unmanaged<int, float, TrackedPose*, uint, void>)Slot(system, 12))(2, 0, poses, index + 1);
        return poses[index];
    }

    public TrackedPose RawHeadPose() => HeadPose(2);

    private TrackedPose HeadPose(int trackingOrigin)
    {
        TrackedPose head = default;
        ((delegate* unmanaged<int, float, TrackedPose*, uint, void>)Slot(system, 12))(trackingOrigin, 0, &head, 1);
        return head;
    }

    public CalibrationSnapshot ReadCalibration()
    {
        ((delegate* unmanaged<void>)Slot(setup, 1))(); // Refresh the working copy from the live room.
        Matrix34 origin = default;
        if (((delegate* unmanaged<Matrix34*, byte>)Slot(setup, 7))(&origin) == 0)
            throw new InvalidOperationException($"SteamVR did not provide a standing tracking origin (calibration {Calibration}).");
        var readBounds = (delegate* unmanaged<Vector3*, uint*, byte>)Slot(setup, 4);
        uint count = 0;
        // Some runtimes report an insufficient buffer while still returning the required count.
        bool sizeRead = readBounds(null, &count) != 0;
        if ((!sizeRead && count == 0) || count > 4096)
            throw new InvalidOperationException("SteamVR did not provide readable chaperone bounds.");
        var corners = new Vector3[count * 4];
        if (count != 0)
        {
            uint capacity = count;
            fixed (Vector3* points = corners)
                if (readBounds(points, &capacity) == 0 || capacity != count)
                    throw new InvalidOperationException("SteamVR chaperone bounds changed while reading.");
        }
        Matrix34 seated = default;
        bool hasSeatedOrigin = ((delegate* unmanaged<Matrix34*, byte>)Slot(setup, 6))(&seated) != 0;
        return new(origin, corners, hasSeatedOrigin ? seated : null);
    }

    public void CommitCalibration(CalibrationSnapshot snapshot)
    {
        SetWorkingCalibration(snapshot);
        if (((delegate* unmanaged<int, byte>)Slot(setup, 0))(1) == 0) // Live chaperone
            throw new InvalidOperationException("SteamVR rejected the play-space reset.");
    }

    public void ShowPlaySpacePreview(CalibrationSnapshot snapshot)
    {
        SetWorkingCalibration(snapshot);
        ((delegate* unmanaged<void>)Slot(setup, (int)ChaperoneSetupSlot.ShowWorkingSetPreview))();
    }

    public void HidePlaySpacePreview()
    {
        ((delegate* unmanaged<void>)Slot(setup, (int)ChaperoneSetupSlot.HideWorkingSetPreview))();
        ((delegate* unmanaged<void>)Slot(setup, 1))(); // RevertWorkingCopy
    }

    private void SetWorkingCalibration(CalibrationSnapshot snapshot)
    {
        Matrix34 origin = snapshot.Origin;
        ((delegate* unmanaged<Matrix34*, void>)Slot(setup, 12))(&origin);
        if (snapshot.SeatedOrigin is Matrix34 seated)
            ((delegate* unmanaged<Matrix34*, void>)Slot(setup, 11))(&seated);
        if (snapshot.Corners.Length > 0)
        {
            fixed (Vector3* points = snapshot.Corners)
                ((delegate* unmanaged<Vector3*, uint, void>)Slot(setup, 9))(points, (uint)snapshot.Corners.Length / 4);
        }
    }

    public void Dispose()
    {
        if (!initialized) return;
        HideNameKeyboard();
        if (dashboard != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(dashboard);
        dashboardTexture?.Dispose();
        if (dashboardIcon != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(dashboardIcon);
        if (marker != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(marker);
        if (laser != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(laser);
        foreach (var layer in upperGuides)
            if (layer.Handle != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(layer.Handle);
        if (customGuide != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(customGuide);
        foreach (ulong wallGrid in wallGrids.Concat(wallGridBack))
            if (wallGrid != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(wallGrid);
        if (centerMarker != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(centerMarker);
        if (floorGrid != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(floorGrid);
        if (ceilingGrid != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(ceilingGrid);
        if (ceilingGridBack != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(ceilingGridBack);
        if (circleGuide != 0) ((delegate* unmanaged<ulong, int>)Slot(overlay, 3))(circleGuide);
        VR_ShutdownInternal();
        initialized = false;
    }

    private static nint Interface(string name)
    {
        var pointer = VR_GetGenericInterface("FnTable:" + name, out var error);
        if (pointer == 0 || error != 0)
            throw new InvalidOperationException($"SteamVR interface {name} unavailable ({error}).");
        return pointer;
    }

    // Slots are from Valve's openvr_capi.h for the exact interface versions above.
    private static nint Slot(nint table, int slot) => ((nint*)table)[slot];

    internal enum ChaperoneSetupSlot
    {
        ShowWorkingSetPreview = 17,
        HideWorkingSetPreview = 18
    }

    private static void Check(int error, string operation)
    {
        if (error != 0) throw new InvalidOperationException($"{operation} failed (SteamVR error {error}).");
    }

    private unsafe delegate void StringCall(byte* text);

    private static void UseString(string text, StringCall call)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + '\0');
        fixed (byte* pointer = bytes) call(pointer);
    }

    [DllImport("openvr_api", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint VR_InitInternal(out int error, int applicationType);
    [DllImport("openvr_api", CallingConvention = CallingConvention.Cdecl)]
    private static extern void VR_ShutdownInternal();
    [DllImport("openvr_api", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint VR_GetGenericInterface(string version, out int error);
    [DllImport("openvr_api", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint VR_GetVRInitErrorAsEnglishDescription(int error);
    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string pipe, uint timeoutMilliseconds);

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ActionSet
    {
        [FieldOffset(0)] public ulong Handle;
        [FieldOffset(28)] public int Priority;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct AnalogAction
    {
        [FieldOffset(0)] public byte Active;
        [FieldOffset(16)] public float X;
    }

    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct OriginInfo
    {
        public ulong Path;
        public uint Device;
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct PoseAction
    {
        [FieldOffset(0)] public byte Active;
        [FieldOffset(16)] public TrackedPose Pose;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct DigitalAction
{
    [FieldOffset(0)] public byte Active;
    [FieldOffset(8)] public ulong Origin;
    [FieldOffset(16)] public byte Pressed;
    [FieldOffset(17)] public byte Changed;
    public readonly bool Held => Active != 0 && Pressed != 0;
    public readonly bool JustPressed => Held && Changed != 0;
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct TrackedPose
{
    [FieldOffset(0)] public Matrix34 Matrix;
    [FieldOffset(72)] public int TrackingResult;
    [FieldOffset(76)] public byte Valid;
    [FieldOffset(77)] public byte Connected;
    public readonly bool Tracked => Valid != 0 && Connected != 0 && TrackingResult == 200;
}
