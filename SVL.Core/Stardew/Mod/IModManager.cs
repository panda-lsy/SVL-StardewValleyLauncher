using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Mod;

public interface IModManager
{
    Task<List<SdVMod>> LoadModsAsync(string modsPath);
    Task<bool> InstallModAsync(string modPath, string destinationModsPath);
    Task<bool> UninstallModAsync(string modId, string modsPath);
    Task<bool> EnableModAsync(string modId, string modsPath);
    Task<bool> DisableModAsync(string modId, string modsPath);
    Task<bool> ValidateManifestAsync(string modPath);
    Task CheckModUpdatesAsync(List<SdVMod> mods);

    /// <summary>
    /// 检测嵌套文件夹问题（manifest.json 在子目录中）
    /// </summary>
    Task<List<ModManager.NestedFolderIssue>> DetectNestedFolderIssuesAsync(string modsPath);

    /// <summary>
    /// 修复嵌套文件夹问题
    /// </summary>
    Task<bool> FixNestedFolderAsync(ModManager.NestedFolderIssue issue);
}
