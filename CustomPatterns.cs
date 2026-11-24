using System.Numerics;
using System.Text.Json;

internal readonly record struct CustomEditorState(bool Recording, bool Drag, bool LockHeight, int PointCount,
    int IntervalMs, string? Name = null, bool Naming = false, bool ConfirmDelete = false, int Page = 0,
    string[]? Names = null, int Selected = -1, bool Repeat = true, bool RandomOrder = false, bool SaveRotation = true, string? HeadsetPose = null, bool SaveHeight = true, SavedPattern? Pattern = null, int EditPoint = -1, bool Drag3D = false);

internal sealed record SavedPattern(Vector3[] Points, int IntervalMs, string? Name = null, float[]? Yaws = null)
{
    public const int DefaultIntervalMs = 500;

    private readonly bool? useRecordedHeight;
    public bool UseRecordedHeight
    {
        get => useRecordedHeight ?? Yaws is not null;
        init => useRecordedHeight = value;
    }
    public bool SmoothTransition { get; init; }
    public int[]? TravelTimesMs { get; init; }
    public int TravelTimeAt(int index) => EditedValues is { } edited && index < edited.Length && edited[index] is { Length: 7 } row
        ? Math.Clamp((int)(row[6] ?? IntervalMs), 10, 60000) : TravelTimesMs is { } times && index < times.Length
        ? Math.Clamp(times[index], 10, 60000) : Math.Clamp(IntervalMs, 10, 60000);

    // Nullable overrides preserve the distinction between zero and an ignored field.
    public float?[][]? EditedValues { get; init; }

    public float? CellValue(int point, int field)
    {
        if (EditedValues is { } edited && point < edited.Length && edited[point] is { Length: 7 } values)
            return values[field];
        return field switch
        {
            0 => Points[point].X,
            1 => UseRecordedHeight ? Points[point].Y : null,
            2 => Points[point].Z,
            3 => Yaws is { } yaws ? yaws[point] * 180 / MathF.PI : null,
            6 => TravelTimeAt(point),
            _ => null
        };
    }

    public string CellText(int point, int field) => CellValue(point, field)?.ToString("0.######") ?? "-";

    public SavedPattern AppendPoint()
    {
        var rows = Enumerable.Range(0, Points.Length)
            .Select(index => Enumerable.Range(0, 7).Select(field => CellValue(index, field)).ToArray()).ToList();
        var last = Points[^1];
        rows.Add([last.X, null, last.Z, null, null, null, null]);
        return this with { Points = [.. Points, last with { Y = 0 }], Yaws = null,
            UseRecordedHeight = false, EditedValues = rows.ToArray() };
    }

    public bool Repeat { get; init; } = true;
    public bool RandomOrder { get; init; }
}

internal sealed class PatternLibrary(string legacyPath)
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private readonly string folder = Path.ChangeExtension(legacyPath, null);
    private readonly Dictionary<int, string> files = [];

    public static PatternLibrary ForAppFolder() => new(Path.Combine(AppContext.BaseDirectory, "custom-patterns.json"));

    public Dictionary<int, SavedPattern> Read()
    {
        Migrate();
        var patterns = new Dictionary<int, SavedPattern>();
        if (!Directory.Exists(folder)) return patterns;
        foreach (string file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            SavedPattern? pattern;
            try { pattern = JsonSerializer.Deserialize<SavedPattern>(File.ReadAllText(file), Json); }
            catch (JsonException) { continue; }
            catch (FileNotFoundException) { continue; }
            if (pattern?.Points is not { Length: > 0 and <= 256 }) continue;

            int slot = files.FirstOrDefault(pair => PathsEqual(pair.Value, file)).Key;
            if (slot == 0)
            {
                slot = files.Count == 0 ? 1 : files.Keys.Max() + 1;
                files.Add(slot, file);
            }
            patterns.Add(slot, string.IsNullOrWhiteSpace(pattern.Name)
                ? pattern with { Name = Path.GetFileNameWithoutExtension(file) } : pattern);
        }
        return patterns;
    }

    public SavedPattern? Load(int slot)
    {
        if (!Read().TryGetValue(slot, out SavedPattern? pattern)) return null;
        if (pattern.Points is not { Length: > 0 and <= 256 } ||
            pattern.Points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) ||
            (pattern.Yaws is { } yaws && (yaws.Length != pattern.Points.Length || yaws.Any(yaw => !float.IsFinite(yaw)))))
            throw new InvalidOperationException($"Slot {slot} contains invalid points.");
        if (pattern.EditedValues is { } rows &&
            (rows.Length != pattern.Points.Length || rows.Any(row =>
                row is not { Length: 7 } || row.Any(value => value is float number &&
                    (!float.IsFinite(number) || MathF.Abs(number) > 100000)))))
            throw new InvalidOperationException($"Slot {slot} contains invalid edited point values.");
        return pattern with { IntervalMs = Math.Clamp(pattern.IntervalMs, 10, 60000) };
    }

    public void Save(int slot, SavedPattern pattern)
    {
        var existing = Read();
        files.TryGetValue(slot, out string? oldFile);
        Directory.CreateDirectory(folder);
        string destination = existing.TryGetValue(slot, out var old) && old.Name == pattern.Name && oldFile is not null
            ? oldFile : AvailableFile(FileStem(pattern.Name, slot), oldFile);
        Write(destination, JsonSerializer.Serialize(pattern, Json), overwrite: oldFile is not null && PathsEqual(oldFile, destination));
        if (oldFile is not null && !PathsEqual(oldFile, destination))
        {
            try { File.Delete(oldFile); }
            catch
            {
                File.Delete(destination);
                throw;
            }
        }
        files[slot] = destination;
    }

    public void Delete(int slot)
    {
        Read();
        if (!files.TryGetValue(slot, out string? file)) return;
        File.Delete(file);
        files.Remove(slot);
    }

    private void Migrate()
    {
        if (!File.Exists(legacyPath)) return;
        var legacy = JsonSerializer.Deserialize<Dictionary<int, SavedPattern>>(File.ReadAllText(legacyPath), Json) ?? [];
        Directory.CreateDirectory(folder);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, pattern) in legacy.OrderBy(pair => pair.Key))
        {
            string contents = JsonSerializer.Serialize(pattern, Json);
            string stem = FileStem(pattern.Name, slot);
            for (int suffix = 1; ; suffix++)
            {
                string file = Candidate(stem, suffix);
                if (claimed.Contains(file)) continue;
                if (File.Exists(file))
                {
                    if (File.ReadAllText(file) != contents) continue;
                }
                else Write(file, contents, overwrite: false);
                claimed.Add(file);
                break;
            }
        }
        string backup = legacyPath + ".migrated";
        if (File.Exists(backup)) backup += "." + Guid.NewGuid().ToString("N");
        File.Move(legacyPath, backup);
    }

    private static string FileStem(string? name, int slot)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"Pattern {slot}";
        var invalid = Path.GetInvalidFileNameChars();
        string stem = new string(name.Select(c => invalid.Contains(c) ? '_' : c).Take(100).ToArray()).Trim().TrimEnd('.');
        if (stem.Length == 0) return $"Pattern {slot}";
        string device = stem.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsDigit(device[3])))
            stem = "_" + stem;
        return stem;
    }

    private string Candidate(string stem, int suffix) =>
        Path.Combine(folder, suffix == 1 ? $"{stem}.json" : $"{stem} ({suffix}).json");

    private string AvailableFile(string stem, string? current)
    {
        for (int suffix = 1; ; suffix++)
        {
            string candidate = Candidate(stem, suffix);
            if (current is not null && PathsEqual(candidate, current)) return current;
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool PathsEqual(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static void Write(string file, string contents, bool overwrite)
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, file, overwrite);
        }
        finally { File.Delete(temporary); }
    }

}

internal sealed class CustomPlayback
{
    private readonly SavedPattern pattern;
    private readonly int[] order;

    public Vector3 ResolvePoint(Vector3 point, int index, Vector3 current)
    {
        int i = index - 1;
        if (pattern.CellValue(i, 0) is null) point.X = current.X;
        if (pattern.CellValue(i, 1) is null) point.Y = current.Y;
        if (pattern.CellValue(i, 2) is null) point.Z = current.Z;
        return point;
    }

    public CustomPlayback(SavedPattern pattern, Vector3? start = null)
    {
        if (start is Vector3 destination)
        {
            Vector3 offset = destination - pattern.Points[0];
            offset.Y = 0;
            pattern = pattern with { Points = pattern.Points.Select(point => point + offset).ToArray() };
        }
        this.pattern = pattern;
        order = Enumerable.Range(0, pattern.Points.Length).ToArray();
        if (pattern.RandomOrder)
        {
            FloorPattern.Shuffle(order);
            if (start.HasValue) (order[0], order[Array.IndexOf(order, 0)]) = (0, order[0]);
        }
    }

    private int next;
    private long? deadline;
    public bool Finished => next == order.Length;
    public bool Due(long now) => !Finished && (deadline is null || now >= deadline);

    public (Vector3 Point, int Index) TakePoint(long now)
    {
        int index = order[next++];
        if (Finished && pattern.Repeat)
        {
            next = 0;
            if (pattern.RandomOrder) FloorPattern.Shuffle(order, index);
        }
        long scheduled = (deadline ?? now) + pattern.IntervalMs;
        deadline = scheduled > now ? scheduled : now + pattern.IntervalMs;
        return (pattern.Points[index], index + 1);
    }

    private (Vector3 From, Vector3 To, Vector3 FromAngles, Vector3 ToAngles, int Index, long Start, int Duration)? segment;
    public Matrix34? TransitionHead { get; private set; }
    public bool SmoothTransition => pattern.SmoothTransition;

    private void StartSegment(long now, Vector3 from, Vector3 angles)
    {
        var (point, index) = TakePoint(now);
        var to = new Vector3(AngleAt(index, 4) ?? angles.X, YawAt(index) ?? angles.Y, AngleAt(index, 5) ?? angles.Z);
        segment = (from, ResolvePoint(point, index, from), angles, to, index, now, pattern.TravelTimeAt(index - 1));
    }

    public (Vector3 Point, Vector3 Angles, int Index, bool Complete) Sample(long now, Matrix34 standing, Matrix34 raw)
    {
        if (segment is null)
        {
            TransitionHead = raw;
            StartSegment(now, standing.Position, PlaySpace.HeadAngles(standing));
        }
        var travel = segment!.Value;
        float progress = Math.Clamp((now - travel.Start) / (float)travel.Duration, 0, 1);
        var delta = travel.ToAngles - travel.FromAngles;
        var angles = travel.FromAngles + progress * new Vector3(MathF.IEEERemainder(delta.X, MathF.Tau),
            MathF.IEEERemainder(delta.Y, MathF.Tau), MathF.IEEERemainder(delta.Z, MathF.Tau));
        bool complete = progress >= 1 && Finished;
        if (progress >= 1 && !complete)
            StartSegment(now, travel.To, angles);
        return (Vector3.Lerp(travel.From, travel.To, progress), angles, travel.Index, complete);
    }

    public int Count => pattern.Points.Length;
    public float? AngleAt(int index, int field) => pattern.CellValue(index - 1, field) is float degrees
        ? degrees * MathF.PI / 180 : null;
    public float? YawAt(int index) => AngleAt(index, 3);
    public bool HasHeight(int index) => pattern.CellValue(index - 1, 1).HasValue;
}
