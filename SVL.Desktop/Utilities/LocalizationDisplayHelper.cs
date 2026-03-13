using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SVL.Core.Logging;
using SVL.Core.Stardew.Localization;
using SVL.Desktop.Models;

namespace SVL.Desktop.Utilities;

public static class LocalizationDisplayHelper
{
    public static async Task ApplyLocalizationAsync(IEnumerable<ModSearchItem> items, bool forceRefresh = false)
    {
        if (items == null)
            return;

        var tasks = items.Select(item => ApplyLocalizationAsync(item, forceRefresh));
        await Task.WhenAll(tasks);
    }

    public static void ApplyLocalizationInBackground(IEnumerable<ModSearchItem> items, bool forceRefresh = false)
    {
        if (items == null)
            return;

        var itemList = items
            .Where(item => item != null)
            .ToList();

        if (itemList.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyLocalizationAsync(itemList, forceRefresh).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("[LocalizationDisplayHelper] 后台应用本地化失败", ex);
            }
        });
    }

    public static async Task ApplyLocalizationAsync(ModSearchItem? item, bool forceRefresh = false)
    {
        if (item == null)
            return;

        if (!TryResolveRequest(item, out var entityType, out var platform, out var id))
            return;

        try
        {
            var localization = await CommunityLocalizationService.GetAsync(entityType, platform, id, forceRefresh).ConfigureAwait(false);
            if (localization == null)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => item.ApplyLocalization(
                    localization.Name?.ZhCn,
                    localization.Name?.Source,
                    localization.Description?.ZhCn,
                    localization.Description?.Source,
                    localization.Meta?.Contributor,
                    localization.Meta?.UpdatedAt));
                return;
            }

            item.ApplyLocalization(
                localization.Name?.ZhCn,
                localization.Name?.Source,
                localization.Description?.ZhCn,
                localization.Description?.Source,
                localization.Meta?.Contributor,
                localization.Meta?.UpdatedAt);
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalizationDisplayHelper] 应用本地化失败: {item.Id}", ex);
        }
    }

    public static bool TryResolveRequest(ModSearchItem item, out string entityType, out string platform, out string id)
    {
        entityType = string.Empty;
        platform = string.Empty;
        id = string.Empty;

        if (item == null)
            return false;

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("curse-", StringComparison.OrdinalIgnoreCase))
        {
            entityType = "mod";
            platform = "Curseforge";
            id = item.Id.Substring("curse-".Length);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("nexus-", StringComparison.OrdinalIgnoreCase))
        {
            entityType = "mod";
            platform = "NexusMods";
            id = item.Id.Substring("nexus-".Length);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("cfpack-", StringComparison.OrdinalIgnoreCase))
        {
            entityType = "modpack";
            platform = "Curseforge";
            id = item.Id.Substring("cfpack-".Length);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("nexuscol-", StringComparison.OrdinalIgnoreCase))
        {
            entityType = "collection";
            platform = "NexusMods";
            id = ExtractCollectionSlug(item);
            return !string.IsNullOrWhiteSpace(id);
        }

        if (string.Equals(item.Id, "github-smapi", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(item.Name) && item.Name.IndexOf("SMAPI", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            entityType = "mod";
            platform = "NexusMods";
            id = "2400";
            return true;
        }

        return false;
    }

    public static string ExtractCollectionSlug(ModSearchItem item)
    {
        if (item == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
            {
                var segments = uri.AbsolutePath
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

                var collectionsIndex = segments.FindIndex(segment => string.Equals(segment, "collections", StringComparison.OrdinalIgnoreCase));
                if (collectionsIndex >= 0 && collectionsIndex + 1 < segments.Count)
                    return segments[collectionsIndex + 1];
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("nexuscol-", StringComparison.OrdinalIgnoreCase))
            return item.Id.Substring("nexuscol-".Length);

        return string.Empty;
    }
}