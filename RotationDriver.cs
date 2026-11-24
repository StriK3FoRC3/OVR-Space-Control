using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;

internal sealed class RotationDriver : IDisposable
{
    private const string ChannelName = @"Local\OVRSpaceControl.Poses.v9";
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private long nextPoll;
    private bool holding;
    public bool Holding => holding;
    private bool RequiresDisplayCorrection => view?.ReadInt32(160) == 1;
    public bool Ready => view is not null && (!RequiresDisplayCorrection ||
        (view.ReadInt32(16) != 0 && unchecked((uint)(Environment.TickCount - view.ReadInt32(16))) < 1000));
    public bool Registered { get; private set; }
    public string? InstallMessage { get; private set; }
    public bool Busy { get; private set; }
    private static string Folder => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SteamVR", "driver"));
    private static string RegistryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "openvr", "openvrpaths.vrpath");

    public void Poll()
    {
        long now = Environment.TickCount64;
        if (now < nextPoll) return;
        nextPoll = now + (Ready ? 200 : 1000);
        if (view is null)
        {
            try
            {
                mapping = MemoryMappedFile.OpenExisting(ChannelName, MemoryMappedFileRights.ReadWrite);
                view = mapping.CreateViewAccessor(0, 168);
                view.Write(4, 0);
            }
            catch (FileNotFoundException) { return; }
            catch (UnauthorizedAccessException) { Dispose(); return; }
        }
        if (view.ReadInt32(0) == 0 || unchecked((uint)(Environment.TickCount - view.ReadInt32(12))) > 2500) { Dispose(); return; }
        if (RequiresDisplayCorrection && !Ready) Finish(false);
        view.Write(8, Environment.TickCount);
    }

    public (System.Numerics.Vector3 Translation, System.Numerics.Quaternion Rotation) ReadOffset()
    {
        if (view is null) return (default, System.Numerics.Quaternion.Identity);
        int sequence = view.ReadInt32(60);
        if (sequence == 0 || (sequence & 1) != 0) return (default, System.Numerics.Quaternion.Identity);
        Thread.MemoryBarrier();
        System.Numerics.Vector3 translation = new(view.ReadSingle(64), view.ReadSingle(68), view.ReadSingle(72));
        System.Numerics.Quaternion rotation = new(view.ReadSingle(76), view.ReadSingle(80), view.ReadSingle(84), view.ReadSingle(88));
        Thread.MemoryBarrier();
        if (sequence == view.ReadInt32(60) && float.IsFinite(rotation.LengthSquared()) && rotation.LengthSquared() > .5f &&
            float.IsFinite(translation.LengthSquared()))
            return (translation, rotation);
        return (default, System.Numerics.Quaternion.Identity);
    }
    public void Hold(uint device, int quarterSteps, int accelerationSteps, uint pivotDevice)
    {
        if (device is 0 or >= 64) { Finish(false); return; }
        view?.Write(8, Environment.TickCount);
        view?.Write(20, accelerationSteps);
        view?.Write(164, pivotDevice < 64 ? (int)pivotDevice + 1 : 0);
        view?.Write(4, 16 + (int)device + 64 * (quarterSteps - 1));
        holding = true;
    }

    public void StopMotion() { if (view is not null && view.ReadInt32(4) != 0) view.Write(4, 3); holding = false; }

    public void Finish(bool cancel, int resistance = 100, int spinResistance = 100, Matrix34? floor = null, float floorOffsetMeters = 0, int gravity = 0, bool bounce = false, float ceiling = 0, IReadOnlyList<DragWall>? walls = null)
    {
        if (holding && view is not null)
        {
            view.Write(24, spinResistance);
            if (floor is Matrix34 origin)
            {
                var up = origin.Rotate(System.Numerics.Vector3.UnitY);
                view.Write(32, up.X); view.Write(36, up.Y); view.Write(40, up.Z);
                view.Write(44, System.Numerics.Vector3.Dot(up, origin.Position));
            }
            view.Write(28, floor.HasValue && bounce ? 1 : 0);
            view.Write(48, floor.HasValue ? gravity : 0);
            view.Write(52, floorOffsetMeters);
            view.Write(56, floor.HasValue ? ceiling : 0);
            int wallCount = floor.HasValue && walls is not null ? Math.Min(4, walls.Count) : 0;
            for (int index = 0; index < wallCount; index++)
            {
                var wall = walls![index];
                var wallOrigin = floor!.Value;
                var normal = wallOrigin.Rotate(wall.Normal);
                var center = wallOrigin.Position + wallOrigin.Rotate(wall.Center);
                int offset = 96 + index * 16;
                view.Write(offset, normal.X);
                view.Write(offset + 4, normal.Y);
                view.Write(offset + 8, normal.Z);
                view.Write(offset + 12, System.Numerics.Vector3.Dot(normal, center));
            }
            view.Write(92, wallCount);
            view.Write(4, cancel ? 3 : resistance >= 100 && spinResistance >= 100 && gravity == 0 && ceiling == 0 && !bounce ? 2 : -1 - resistance);
        }
        holding = false;
    }

    public void Reset()
    {
        view?.Write(4, 0);
        holding = false;
    }

    public void Dispose()
    {
        Reset();
        view?.Dispose();
        view = null;
        mapping?.Dispose();
        mapping = null;
    }

    private const string DriverName = "OVRSpaceControl";
    private const string MisspelledDriverName = "OVRSpaceControll";
    private const string LegacyDriverName = "spaceteleporter";
    private const string PreviousDriverName = "SpaceTeleporterPlus";

    private static string[] RegisteredFolders(JsonElement registry) =>
        registry.TryGetProperty("external_drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array
            ? drivers.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!).Where(IsTeleporterFolder).ToArray()
            : [];

    private static bool IsTeleporterFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(Folder, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "driver.vrdrivermanifest")));
            return manifest.RootElement.ValueKind == JsonValueKind.Object &&
                manifest.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                (string.Equals(name.GetString(), DriverName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name.GetString(), MisspelledDriverName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name.GetString(), LegacyDriverName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name.GetString(), PreviousDriverName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void ReadRegistration()
    {
        if (!File.Exists(RegistryPath)) { Registered = false; return; }
        using var registry = JsonDocument.Parse(File.ReadAllText(RegistryPath));
        Registered = RegisteredFolders(registry.RootElement).Length > 0;
    }

    public async Task ChangeRegistration()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            using var registry = JsonDocument.Parse(File.ReadAllText(RegistryPath));
            string[] registeredFolders = RegisteredFolders(registry.RootElement);
            bool remove = registeredFolders.Length > 0;
            if (!remove && !File.Exists(Path.Combine(Folder, "bin", "win64", $"driver_{DriverName}.dll")))
                throw new IOException("The bundled OVR Space Control driver is missing.");
            var utility = registry.RootElement.GetProperty("runtime").EnumerateArray()
                .Select(entry => Path.Combine(entry.GetString()!, "bin", "win64", "vrpathreg.exe")).FirstOrDefault(File.Exists)
                ?? throw new IOException("Start SteamVR once so its registration tool can be located.");
            foreach (string path in remove ? registeredFolders : [Folder])
            {
                var info = new ProcessStartInfo(utility) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(remove ? "removedriver" : "adddriver");
                info.ArgumentList.Add(path);
                using var process = Process.Start(info) ?? throw new IOException("Could not start vrpathreg.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new IOException(remove ? "SteamVR could not remove the driver registration." :
                        "SteamVR could not register the driver.");
            }
            ReadRegistration();
            if (Registered == remove)
                throw new IOException(remove ? "SteamVR still lists the driver as registered." :
                    "SteamVR did not list the driver after installation.");
            Reset();
            InstallMessage = remove ? "Driver removed. Restart SteamVR." : "Driver installed. Restart SteamVR.";
        }
        catch (Exception ex) { InstallMessage = ex.Message; }
        finally { Busy = false; }
    }
}
