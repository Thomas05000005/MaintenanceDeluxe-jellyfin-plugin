using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

public class BannerClientConfigTests
{
    [Fact]
    public void From_DropsMaintenanceMode_FromSerializedOutput()
    {
        var src = new PluginConfiguration
        {
            MaintenanceMode = new MaintenanceSetting
            {
                IsActive = true,
                Webhook = new WebhookSettings { Url = "https://discord.com/api/webhooks/SECRET" },
                WhitelistedUserIds = new List<string> { "11111111-1111-1111-1111-111111111111" },
                MaintenanceDisabledUserIds = new List<string> { "22222222-2222-2222-2222-222222222222" },
                PreDisabledUserIds = new List<string> { "33333333-3333-3333-3333-333333333333" }
            }
        };

        var dto = BannerClientConfig.From(src);
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("maintenanceMode", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("33333333-3333-3333-3333-333333333333", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void From_PreservesBannerClientFields()
    {
        var src = new PluginConfiguration
        {
            DisplayDuration = 12,
            PauseDuration = 7,
            ShowDismissButton = false,
            DismissButtonSize = 21,
            ShowDismissAll = false,
            DismissAllSize = 11,
            DismissAllText = "tout cacher",
            ShowInDashboard = true,
            RotationEnabled = false,
            TextAlign = "left",
            RotationShuffle = false,
            PersistDismiss = true,
            PermanentDismissible = true,
            TransitionSpeed = "fast",
            FontSize = 18,
            BannerHeight = 44,
            FontBold = false,
            ShowRefreshPrompt = false,
            UrlPopupHint = "copy on mobile",
            LastModified = 1735689600,
            RotationMessages = new List<BannerMessage>
            {
                new() { Text = "hello", Bg = "#123456", Color = "#abcdef", Enabled = true }
            },
            ColorPresets = new List<ColorPreset>
            {
                new() { Label = "navy", Bg = "#001f3f", Color = "#ffffff" }
            },
            PermanentOverride = new PermanentOverride
            {
                Enabled = true,
                ActiveIndex = 0,
                Entries = new List<PermanentEntry>
                {
                    new() { Text = "perm", Bg = "#000000", Color = "#ffffff" }
                }
            }
        };

        var dto = BannerClientConfig.From(src);

        Assert.Equal(src.DisplayDuration, dto.DisplayDuration);
        Assert.Equal(src.PauseDuration, dto.PauseDuration);
        Assert.Equal(src.ShowDismissButton, dto.ShowDismissButton);
        Assert.Equal(src.DismissButtonSize, dto.DismissButtonSize);
        Assert.Equal(src.ShowDismissAll, dto.ShowDismissAll);
        Assert.Equal(src.DismissAllSize, dto.DismissAllSize);
        Assert.Equal(src.DismissAllText, dto.DismissAllText);
        Assert.Equal(src.ShowInDashboard, dto.ShowInDashboard);
        Assert.Equal(src.RotationEnabled, dto.RotationEnabled);
        Assert.Equal(src.TextAlign, dto.TextAlign);
        Assert.Equal(src.RotationShuffle, dto.RotationShuffle);
        Assert.Equal(src.PersistDismiss, dto.PersistDismiss);
        Assert.Equal(src.PermanentDismissible, dto.PermanentDismissible);
        Assert.Equal(src.TransitionSpeed, dto.TransitionSpeed);
        Assert.Equal(src.FontSize, dto.FontSize);
        Assert.Equal(src.BannerHeight, dto.BannerHeight);
        Assert.Equal(src.FontBold, dto.FontBold);
        Assert.Equal(src.ShowRefreshPrompt, dto.ShowRefreshPrompt);
        Assert.Equal(src.UrlPopupHint, dto.UrlPopupHint);
        Assert.Equal(src.LastModified, dto.LastModified);
        Assert.Single(dto.RotationMessages);
        Assert.Equal("hello", dto.RotationMessages[0].Text);
        Assert.Single(dto.ColorPresets);
        Assert.Equal("navy", dto.ColorPresets[0].Label);
        Assert.True(dto.PermanentOverride.Enabled);
        Assert.Single(dto.PermanentOverride.Entries);
    }

    /// <summary>
    /// Guard against future drift: every JSON property exposed by
    /// <see cref="PluginConfiguration"/> (except the explicitly-stripped
    /// <c>maintenanceMode</c>) MUST also exist on <see cref="BannerClientConfig"/>.
    /// If a future field is added to PluginConfiguration with admin-only intent,
    /// this test still passes (it only checks one direction); but if a banner-facing
    /// field is added and forgotten on the DTO, this test fails fast.
    /// </summary>
    [Fact]
    public void DtoMirrorsAllNonMaintenanceJsonPropertiesOfPluginConfiguration()
    {
        var srcProps = JsonPropertyNames(typeof(PluginConfiguration));
        var dtoProps = JsonPropertyNames(typeof(BannerClientConfig));
        var missing = srcProps
            .Where(p => p != "maintenanceMode")
            .Where(p => !dtoProps.Contains(p))
            .ToList();
        Assert.True(missing.Count == 0, "BannerClientConfig is missing JSON property: " + string.Join(", ", missing));
        Assert.DoesNotContain("maintenanceMode", dtoProps);
    }

    private static HashSet<string> JsonPropertyNames(System.Type t)
    {
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet();
    }
}
