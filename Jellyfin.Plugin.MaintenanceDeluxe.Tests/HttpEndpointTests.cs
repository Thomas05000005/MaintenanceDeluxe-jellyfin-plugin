using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.MaintenanceDeluxe.Api;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SaveAnnouncementsRequest = Jellyfin.Plugin.MaintenanceDeluxe.Api.BannerController.SaveAnnouncementsRequest;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

/// <summary>
/// End-to-end tests of the admin save endpoints (<see cref="BannerController.SaveConfig"/>,
/// <see cref="BannerController.SaveMaintenance"/>, <see cref="BannerController.SaveAdminAnnouncements"/>)
/// driven through a REAL <see cref="Plugin"/> instance backed by a temp-directory
/// <see cref="IApplicationPaths"/> and a real XmlSerializer.
///
/// Why this exists: these endpoints are the validation boundary the rest of the code trusts
/// (CLAUDE.md: "Validation belongs at the boundary"). Pure-function unit tests cover the
/// normalisation helpers in isolation; these tests prove the controller actually WIRES them —
/// rejects with BadRequest, mutates the persisted config, assigns IDs, prunes tracking, and
/// that the result survives a real serialize -> file -> deserialize round-trip through
/// BasePlugin. Constructing the controller + plugin against the real Jellyfin 10.11.x
/// assemblies is itself a compile/load-time compatibility assertion (the API-shape bug class
/// that broke v0.8.0 on 10.11.9).
///
/// Each test gets a fresh Plugin (xUnit news the class per-test) writing to its own temp dir,
/// so the static Plugin.Instance singleton is reset between tests and nothing leaks across.
/// </summary>
public sealed class HttpEndpointTests : IDisposable
{
    private readonly TempPaths _paths;
    private readonly Plugin _plugin;

    public HttpEndpointTests()
    {
        _paths = new TempPaths();
        // Constructing Plugin sets the static Plugin.Instance singleton the controller reads.
        _plugin = new Plugin(_paths, new TestXmlSerializer(), NullLogger<Plugin>.Instance);
    }

    public void Dispose() => _paths.Dispose();

    // userManager/session/httpFactory are null! because the code paths under test
    // (validation + no-transition save) never dereference them. The transition paths
    // that DO use IUserManager are covered by MaintenanceHelperTests (SetUserDisabledAsync
    // + the pure selectors) instead.
    private static BannerController NewController() =>
        new BannerController(null!, null!, null!, NullLogger<BannerController>.Instance);

    // Reads the config XML that BasePlugin.SaveConfiguration() actually wrote to disk
    // (whatever filename it chose) and deserialises it independently, proving the value
    // was persisted — not merely held in the live plugin's in-memory Configuration.
    private PluginConfiguration ReloadFromDisk()
    {
        var file = Directory.GetFiles(_paths.PluginConfigurationsPath, "*.xml");
        Assert.True(file.Length >= 1, "SaveConfiguration did not write a config XML file");
        using var fs = File.OpenRead(file[0]);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(fs)!;
    }

    // ── SaveConfig ───────────────────────────────────────────────────────────────

    [Fact]
    public void SaveConfig_RejectsJavascriptUrlInRotationMessage()
    {
        var cfg = new PluginConfiguration
        {
            RotationMessages = new List<BannerMessage>
            {
                new() { Text = "hi", Url = "javascript:alert(1)" }
            }
        };
        var result = NewController().SaveConfig(cfg);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SaveConfig_RejectsProtocolRelativeUrl()
    {
        var cfg = new PluginConfiguration
        {
            RotationMessages = new List<BannerMessage> { new() { Text = "x", Url = "//evil.com/x" } }
        };
        Assert.IsType<BadRequestObjectResult>(NewController().SaveConfig(cfg));
    }

    [Fact]
    public void SaveConfig_ClampsBannerHeight_AndPersists()
    {
        var cfg = new PluginConfiguration { BannerHeight = 9999 };
        var result = NewController().SaveConfig(cfg);
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(80, Plugin.Instance!.Configuration.BannerHeight);
        Assert.Equal(80, ReloadFromDisk().BannerHeight); // survived serialize -> file -> deserialize
    }

    [Fact]
    public void SaveConfig_NormalisesUnknownTextAlignToCenter()
    {
        var cfg = new PluginConfiguration { TextAlign = "diagonal" };
        Assert.IsType<NoContentResult>(NewController().SaveConfig(cfg));
        Assert.Equal("center", Plugin.Instance!.Configuration.TextAlign);
    }

    [Fact]
    public void SaveConfig_RebasesInvalidHexColourToDefault()
    {
        var cfg = new PluginConfiguration
        {
            RotationMessages = new List<BannerMessage>
            {
                new() { Text = "x", Bg = "red;position:fixed", Color = "not-a-colour" }
            }
        };
        Assert.IsType<NoContentResult>(NewController().SaveConfig(cfg));
        var saved = Plugin.Instance!.Configuration.RotationMessages[0];
        Assert.Equal("#1976d2", saved.Bg);
        Assert.Equal("#ffffff", saved.Color);
    }

    [Fact]
    public void SaveConfig_DoesNotClobberLiveMaintenanceState()
    {
        // Arrange: maintenance currently active in the live config.
        Plugin.Instance!.Configuration.MaintenanceMode.IsActive = true;
        Plugin.Instance.Configuration.MaintenanceMode.Message = "we are down";

        // Act: a SaveConfig payload carrying a different (blank) maintenance block.
        var cfg = new PluginConfiguration { MaintenanceMode = new MaintenanceSetting { IsActive = false } };
        Assert.IsType<NoContentResult>(NewController().SaveConfig(cfg));

        // Assert: the live maintenance state was preserved, not overwritten by the payload.
        Assert.True(Plugin.Instance.Configuration.MaintenanceMode.IsActive);
        Assert.Equal("we are down", Plugin.Instance.Configuration.MaintenanceMode.Message);
    }

    // ── SaveMaintenance (validation + no-transition) ───────────────────────────────

    [Fact]
    public async Task SaveMaintenance_RejectsUnsafeStatusUrl()
    {
        var m = new MaintenanceSetting { StatusUrl = "javascript:alert(1)" };
        Assert.IsType<BadRequestObjectResult>(await NewController().SaveMaintenance(m));
    }

    [Fact]
    public async Task SaveMaintenance_RejectsScheduledEndBeforeStart()
    {
        var m = new MaintenanceSetting
        {
            ScheduledStart = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ScheduledEnd = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        Assert.IsType<BadRequestObjectResult>(await NewController().SaveMaintenance(m));
    }

    [Fact]
    public async Task SaveMaintenance_RejectsPlainHttpWebhook()
    {
        var m = new MaintenanceSetting
        {
            Webhook = new WebhookSettings { Url = "http://example.com/hook" }
        };
        Assert.IsType<BadRequestObjectResult>(await NewController().SaveMaintenance(m));
    }

    [Fact]
    public async Task SaveMaintenance_NoTransition_PersistsAndClampsOpacity()
    {
        var m = new MaintenanceSetting
        {
            IsActive = false,            // live is false too -> no transition -> no IUserManager
            Message = "back soon",
            CardOpacity = 5.0            // out of [0.40, 1.00]
        };
        var result = await NewController().SaveMaintenance(m);
        Assert.IsType<NoContentResult>(result);
        var saved = Plugin.Instance!.Configuration.MaintenanceMode;
        Assert.Equal("back soon", saved.Message);
        Assert.Equal(1.00, saved.CardOpacity);
        Assert.Equal(1.00, ReloadFromDisk().MaintenanceMode.CardOpacity);
    }

    // ── SaveAdminAnnouncements ─────────────────────────────────────────────────────

    [Fact]
    public void SaveAdminAnnouncements_RejectsUnsafeCtaUrl()
    {
        var body = new SaveAnnouncementsRequest
        {
            Announcements = new List<Announcement> { new() { Title = "T", CtaUrl = "javascript:x" } }
        };
        Assert.IsType<BadRequestObjectResult>(NewController().SaveAdminAnnouncements(body));
    }

    [Fact]
    public void SaveAdminAnnouncements_RejectsUnsafeImageUrl()
    {
        var body = new SaveAnnouncementsRequest
        {
            Announcements = new List<Announcement> { new() { Title = "T", ImageUrl = "//evil.com/x.png" } }
        };
        Assert.IsType<BadRequestObjectResult>(NewController().SaveAdminAnnouncements(body));
    }

    [Fact]
    public void SaveAdminAnnouncements_AssignsGuidToNewEntry_AndClampsExpireDays()
    {
        var body = new SaveAnnouncementsRequest
        {
            Announcements = new List<Announcement>
            {
                new() { Id = "", Title = "New", ExpireAfterDays = 4000 },
                new() { Id = "", Title = "Zero", ExpireAfterDays = 0 }
            }
        };
        Assert.IsType<NoContentResult>(NewController().SaveAdminAnnouncements(body));
        var saved = Plugin.Instance!.Configuration.Announcements;
        Assert.All(saved, a => Assert.True(Guid.TryParse(a.Id, out _)));
        Assert.Equal(365, saved[0].ExpireAfterDays);   // 4000 clamped to 365
        Assert.Null(saved[1].ExpireAfterDays);         // 0 -> null (never expires)
    }

    [Fact]
    public void SaveAdminAnnouncements_CapsComparisonsAtTwenty()
    {
        var comparisons = new List<AnnouncementComparison>();
        for (int i = 0; i < 30; i++)
            comparisons.Add(new AnnouncementComparison { Label = "L" + i });
        var body = new SaveAnnouncementsRequest
        {
            Announcements = new List<Announcement>
            {
                new() { Id = "a", Title = "T", Comparisons = comparisons }
            }
        };
        Assert.IsType<NoContentResult>(NewController().SaveAdminAnnouncements(body));
        Assert.Equal(20, Plugin.Instance!.Configuration.Announcements[0].Comparisons.Count);
    }

    [Fact]
    public void SaveAdminAnnouncements_PrunesOrphanedSeenTracking()
    {
        // Seed seen-tracking for an announcement id that will NOT be in the new list.
        Plugin.Instance!.Configuration.AnnouncementsSeen = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "old-id", UserIds = new List<string> { Guid.NewGuid().ToString() } }
        };
        var body = new SaveAnnouncementsRequest
        {
            Announcements = new List<Announcement> { new() { Id = "kept-id", Title = "Kept" } }
        };
        Assert.IsType<NoContentResult>(NewController().SaveAdminAnnouncements(body));
        Assert.DoesNotContain(Plugin.Instance.Configuration.AnnouncementsSeen, e => e.AnnouncementId == "old-id");
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────────

    /// <summary>Minimal <see cref="IApplicationPaths"/> backed by a throwaway temp directory.
    /// Only PluginConfigurationsPath is meaningfully used (BasePlugin reads/writes the config
    /// XML there); the rest point inside the same root to keep them non-null.</summary>
    private sealed class TempPaths : IApplicationPaths, IDisposable
    {
        public TempPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "mdlx-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PluginConfigurationsPath);
        }

        public string Root { get; }
        public string ProgramDataPath => Root;
        public string WebPath => Root;
        public string ProgramSystemPath => Root;
        public string DataPath => Root;
        public string ImageCachePath => Path.Combine(Root, "imagecache");
        public string PluginsPath => Path.Combine(Root, "plugins");
        public string PluginConfigurationsPath => Path.Combine(Root, "plugins", "configurations");
        public string LogDirectoryPath => Root;
        public string ConfigurationDirectoryPath => Root;
        public string SystemConfigurationFilePath => Path.Combine(Root, "system.xml");
        public string CachePath => Path.Combine(Root, "cache");
        public string TempDirectory => Path.Combine(Root, "temp");
        public string VirtualDataPath => Root;
        public string TrickplayPath => Path.Combine(Root, "trickplay");
        public string BackupPath => Path.Combine(Root, "backup");

        // Not exercised by the config save path under test — present only to satisfy the interface.
        public void MakeSanityCheckOrThrow() { }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive) { }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Real XmlSerializer-backed <see cref="IXmlSerializer"/> (same mechanism Jellyfin
    /// uses for plugin config), so SaveConfiguration writes real XML to disk and ReloadProbe
    /// reads it back.</summary>
    private sealed class TestXmlSerializer : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream)
            => new XmlSerializer(type).Deserialize(stream);

        public object? DeserializeFromFile(Type type, string file)
        {
            using var fs = File.OpenRead(file);
            return new XmlSerializer(type).Deserialize(fs);
        }

        public object? DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            return new XmlSerializer(type).Deserialize(ms);
        }

        public void SerializeToStream(object obj, Stream stream)
            => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            using var fs = File.Create(file);
            new XmlSerializer(obj.GetType()).Serialize(fs, obj);
        }
    }
}
