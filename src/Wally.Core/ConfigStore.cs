using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wally.Core;

/// <summary>
/// JSON persistence for <see cref="WallyConfig"/>.
///
/// Serialises the immutable record tree to/from a single JSON file. Writes are
/// atomic (temp file + replace) so a crash mid-write cannot corrupt the config.
/// Load validates structure and fails fast with a clear message on bad data.
/// </summary>
public static class ConfigStore
{
    private const int SchemaVersion = 1;

    public static string DefaultConfigPath()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var baseDir = string.IsNullOrEmpty(appData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : appData;
        return Path.Combine(baseDir, "wally", "config.json");
    }

    private static readonly IReadOnlyDictionary<string, WallpaperFit> FitByName =
        new Dictionary<string, WallpaperFit>(StringComparer.OrdinalIgnoreCase)
        {
            ["center"] = WallpaperFit.Center,
            ["tile"] = WallpaperFit.Tile,
            ["stretch"] = WallpaperFit.Stretch,
            ["fit"] = WallpaperFit.Fit,
            ["fill"] = WallpaperFit.Fill,
            ["span"] = WallpaperFit.Span,
        };

    private static string FitName(WallpaperFit fit) => fit.ToString().ToLowerInvariant();

    private static WallpaperFit ParseFit(string value)
    {
        if (FitByName.TryGetValue(value, out var fit))
        {
            return fit;
        }

        throw new FormatException($"Unknown wallpaper fit: '{value}'");
    }

    public static JsonObject ToJson(WallyConfig config)
    {
        var setupRules = new JsonArray();
        foreach (var r in config.SetupRules)
        {
            var perMonitor = new JsonArray();
            foreach (var m in r.PerMonitor)
            {
                var mObj = new JsonObject
                {
                    ["edid_key"] = m.EdidKey,
                    ["image_path"] = m.ImagePath,
                    ["fit"] = FitName(m.Fit),
                    ["rotation_degrees"] = m.RotationDegrees,
                };

                // Only persist a pan offset when it is actually set, so existing
                // configs stay clean and the field is purely additive.
                if (m.OffsetX != 0)
                {
                    mObj["offset_x"] = m.OffsetX;
                }

                if (m.OffsetY != 0)
                {
                    mObj["offset_y"] = m.OffsetY;
                }

                perMonitor.Add(mObj);
            }

            setupRules.Add(new JsonObject
            {
                ["setup_fingerprint"] = r.SetupFingerprint,
                ["default_image"] = r.DefaultImage,
                ["default_fit"] = FitName(r.DefaultFit),
                ["per_monitor"] = perMonitor,
            });
        }

        var assignments = new JsonArray();
        foreach (var a in config.DesktopAssignments)
        {
            var aObj = new JsonObject
            {
                ["desktop_guid"] = a.DesktopGuid,
                ["image_path"] = a.ImagePath,
                ["fit"] = FitName(a.Fit),
            };
            if (a.RotationDegrees != 0)
            {
                aObj["rotation_degrees"] = a.RotationDegrees;
            }

            assignments.Add(aObj);
        }

        var rotation = new JsonArray();
        foreach (var img in config.RotationPool)
        {
            rotation.Add(img);
        }

        return new JsonObject
        {
            ["schema"] = SchemaVersion,
            ["new_desktop_behavior"] = config.NewDesktopBehavior,
            ["gallery_folder"] = config.GalleryFolder,
            ["accent_color"] = config.AccentColor,
            ["rotation_pool"] = rotation,
            ["setup_rules"] = setupRules,
            ["desktop_assignments"] = assignments,
        };
    }

    public static WallyConfig FromJson(JsonNode? root)
    {
        if (root is not JsonObject data)
        {
            throw new FormatException("Config root must be a JSON object");
        }

        var schema = data["schema"]?.GetValue<int?>();
        if (schema != SchemaVersion)
        {
            throw new FormatException(
                $"Unsupported config schema {schema?.ToString() ?? "null"}; expected {SchemaVersion}");
        }

        var setupRules = new List<SetupRule>();
        if (data["setup_rules"] is JsonArray rules)
        {
            foreach (var node in rules)
            {
                var r = node!.AsObject();
                var perMonitor = new List<MonitorRule>();
                if (r["per_monitor"] is JsonArray pm)
                {
                    foreach (var mNode in pm)
                    {
                        var m = mNode!.AsObject();
                        perMonitor.Add(new MonitorRule(
                            EdidKey: m["edid_key"]!.GetValue<string>(),
                            ImagePath: m["image_path"]!.GetValue<string>(),
                            Fit: ParseFit(m["fit"]?.GetValue<string>() ?? "fill"),
                            RotationDegrees: m["rotation_degrees"]?.GetValue<int?>() ?? 0,
                            OffsetX: m["offset_x"]?.GetValue<double?>() ?? 0,
                            OffsetY: m["offset_y"]?.GetValue<double?>() ?? 0));
                    }
                }

                setupRules.Add(new SetupRule(
                    SetupFingerprint: r["setup_fingerprint"]!.GetValue<string>(),
                    PerMonitor: perMonitor,
                    DefaultImage: r["default_image"]?.GetValue<string>(),
                    DefaultFit: ParseFit(r["default_fit"]?.GetValue<string>() ?? "fill")));
            }
        }

        var assignments = new List<DesktopAssignment>();
        if (data["desktop_assignments"] is JsonArray asgn)
        {
            foreach (var node in asgn)
            {
                var a = node!.AsObject();
                assignments.Add(new DesktopAssignment(
                    DesktopGuid: a["desktop_guid"]!.GetValue<string>(),
                    ImagePath: a["image_path"]!.GetValue<string>(),
                    Fit: ParseFit(a["fit"]?.GetValue<string>() ?? "fill"),
                    RotationDegrees: a["rotation_degrees"]?.GetValue<int?>() ?? 0));
            }
        }

        var rotation = new List<string>();
        if (data["rotation_pool"] is JsonArray rot)
        {
            foreach (var node in rot)
            {
                rotation.Add(node!.GetValue<string>());
            }
        }

        return new WallyConfig(
            SetupRules: setupRules,
            DesktopAssignments: assignments,
            NewDesktopBehavior: data["new_desktop_behavior"]?.GetValue<string>() ?? "open_manager",
            RotationPool: rotation)
        {
            GalleryFolder = data["gallery_folder"]?.GetValue<string>(),
            AccentColor = data["accent_color"]?.GetValue<string>(),
        };
    }

    private static string ToIndentedJson(JsonObject obj)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            obj.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static WallyConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath();
        if (!File.Exists(path))
        {
            return new WallyConfig();
        }

        try
        {
            var raw = File.ReadAllText(path);
            return FromJson(JsonNode.Parse(raw));
        }
        catch (Exception exc) when (exc is JsonException or FormatException or KeyNotFoundException or NullReferenceException)
        {
            throw new FormatException($"Corrupt or invalid wally config at {path}: {exc.Message}", exc);
        }
    }

    /// <summary>
    /// Read-modify-write: load the LATEST config from disk, apply
    /// <paramref name="change"/>, save, and return the result. Each view keeps
    /// its own in-memory copy; saving that copy directly would overwrite fields
    /// another view changed since (e.g. the accent colour set in Settings being
    /// wiped by the Monitors view saving its gallery folder). Always save through
    /// this so changes compose.
    /// </summary>
    public static WallyConfig Update(Func<WallyConfig, WallyConfig> change, string? path = null)
    {
        WallyConfig latest;
        try { latest = Load(path); }
        catch (FormatException) { latest = new WallyConfig(); }

        var updated = change(latest);
        Save(updated, path);
        return updated;
    }

    public static string Save(WallyConfig config, string? path = null)
    {
        path ??= DefaultConfigPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var payload = ToIndentedJson(ToJson(config));

        // Atomic write: temp file in same dir, then replace.
        var tmp = Path.Combine(dir, Path.GetRandomFileName() + ".tmp");
        try
        {
            File.WriteAllText(tmp, payload);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }

        return path;
    }
}
