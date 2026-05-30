using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

// Jellyfin persists PluginConfiguration to disk via XmlSerializer. None of that path
// was tested: a property that does not round-trip, or a legacy XML that deserialises a
// list to null, would only surface as a crash on a real server upgrade. These tests
// exercise the actual XmlSerializer.
public class XmlRoundTripTests
{
    private static PluginConfiguration RoundTrip(PluginConfiguration src)
    {
        var ser = new XmlSerializer(typeof(PluginConfiguration));
        using var sw = new StringWriter();
        ser.Serialize(sw, src);
        using var sr = new StringReader(sw.ToString());
        return (PluginConfiguration)ser.Deserialize(sr)!;
    }

    [Fact]
    public void PluginConfiguration_IsXmlSerializable_NoThrow()
    {
        // Constructing the serializer throws if any public property type is not
        // XML-serializable. This is the cheapest guard against an un-serialisable field
        // being added (which would break config save/load on every server).
        var ex = Record.Exception(() => new XmlSerializer(typeof(PluginConfiguration)));
        Assert.Null(ex);
    }

    [Fact]
    public void PluginConfiguration_XmlRoundTrip_PreservesAllAnnouncementFields()
    {
        var src = new PluginConfiguration { AnnouncementTheme = "neon" };
        src.Announcements.Add(new Announcement
        {
            Id = "x1",
            Title = "T",
            IsActive = true,
            IsDraft = true,
            ExpireAfterDays = 14,
            ImageUrl = "https://example.com/i.png",
            ImageAlt = "alt",
            Theme = "custom",
            Schedule = new BannerSchedule { Type = "annual", MonthStart = 10, DayStart = 25, MonthEnd = 11, DayEnd = 1 }
        });
        src.CustomAnnouncementTheme = new CustomAnnouncementTheme
        {
            Label = "dark",
            AccentColor = "#000000",
            BorderStyle = "glow"
        };

        var rt = RoundTrip(src);

        Assert.Equal("neon", rt.AnnouncementTheme);
        Assert.Single(rt.Announcements);
        var a = rt.Announcements[0];
        Assert.Equal("x1", a.Id);
        Assert.True(a.IsDraft);
        Assert.Equal(14, a.ExpireAfterDays);
        Assert.Equal("https://example.com/i.png", a.ImageUrl);
        Assert.Equal("alt", a.ImageAlt);
        Assert.Equal("custom", a.Theme);
        Assert.NotNull(a.Schedule);
        Assert.Equal("annual", a.Schedule!.Type);
        Assert.Equal(10, a.Schedule.MonthStart);
        Assert.Equal(1, a.Schedule.DayEnd);
        Assert.NotNull(rt.CustomAnnouncementTheme);
        Assert.Equal("dark", rt.CustomAnnouncementTheme!.Label);
        Assert.Equal("#000000", rt.CustomAnnouncementTheme.AccentColor);
        Assert.Equal("glow", rt.CustomAnnouncementTheme.BorderStyle);
    }

    [Fact]
    public void PluginConfiguration_LegacyXmlMissingNewElements_DeserialisesWithNonNullLists()
    {
        // Upgrade scenario: XML written by a build predating announcements simply lacks
        // <Announcements>/<AnnouncementsSeen>. The ctor defaults must apply so downstream
        // .FirstOrDefault()/.ToDictionary() never NRE (the bug class the v0.7.0 = new()
        // defensives + ctor guard against for the common "element absent" case).
        const string legacy =
            "<?xml version=\"1.0\"?>\n" +
            "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
            "  <DisplayDuration>30</DisplayDuration>\n" +
            "  <BannerHeight>36</BannerHeight>\n" +
            "</PluginConfiguration>";

        var ser = new XmlSerializer(typeof(PluginConfiguration));
        using var sr = new StringReader(legacy);
        var cfg = (PluginConfiguration)ser.Deserialize(sr)!;

        Assert.NotNull(cfg.Announcements);
        Assert.NotNull(cfg.AnnouncementsSeen);
        Assert.Empty(cfg.Announcements);
        Assert.Empty(cfg.AnnouncementsSeen);
        Assert.NotNull(cfg.MaintenanceMode);
        Assert.NotNull(cfg.RotationMessages);
    }

    [Fact]
    public void PluginConfiguration_SeenTrackingRoundTrip_PreservesUserIds()
    {
        var src = new PluginConfiguration();
        src.AnnouncementsSeen.Add(new AnnouncementsSeenEntry
        {
            AnnouncementId = "a1",
            UserIds = new System.Collections.Generic.List<string> { "u1", "u2" }
        });

        var rt = RoundTrip(src);

        Assert.Single(rt.AnnouncementsSeen);
        Assert.Equal("a1", rt.AnnouncementsSeen[0].AnnouncementId);
        Assert.Equal(2, rt.AnnouncementsSeen[0].UserIds.Count);
        Assert.Contains("u1", rt.AnnouncementsSeen[0].UserIds);
    }
}
