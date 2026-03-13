using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SVL.Desktop.ViewModels;

public partial class ModsRightViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;
    private SVL.Core.Stardew.Mod.SdVMod? _currentMod;

    public ModsRightViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        UpdateContent();
    }

    [ObservableProperty]
    private string _status = "请选择一个Mod查看详情";

    [ObservableProperty]
    private string _modName = "";

    [ObservableProperty]
    private string _modVersion = "";

    [ObservableProperty]
    private string _modAuthor = "";

    [ObservableProperty]
    private string _modDescription = "";

    [ObservableProperty]
    private string _modUniqueId = "";

    [ObservableProperty]
    private string _dependencies = "";

    [ObservableProperty]
    private string _conflicts = "";

    [ObservableProperty]
    private bool _hasLocalization;

    public void UpdateContent()
    {
        var selectedMod = _mainViewModel.SelectedMod;
        if (selectedMod == null)
        {
            _currentMod = null;
            Status = "请选择一个Mod查看详情";
            ModName = "";
            ModVersion = "";
            ModAuthor = "";
            ModDescription = "";
            ModUniqueId = "";
            Dependencies = "";
            Conflicts = "";
            HasLocalization = false;
        }
        else
        {
            _currentMod = selectedMod;
            Status = "Mod信息";
            ModName = selectedMod.DisplayName;
            ModVersion = selectedMod.Version;
            ModAuthor = selectedMod.Author ?? "未知";
            ModDescription = string.IsNullOrWhiteSpace(selectedMod.DisplayDescription) ? "暂无描述" : selectedMod.DisplayDescription;
            ModUniqueId = selectedMod.UniqueId;
            Dependencies = FormatDependencies(selectedMod.Manifest?.Dependencies);
            Conflicts = FormatConflicts(selectedMod.ConflictingMods);
            HasLocalization = selectedMod.HasLocalizedName || selectedMod.HasLocalizedDescription;
        }
    }

    [RelayCommand]
    private void ShowLocalizedText()
    {
        if (_currentMod == null)
            return;

        _currentMod.SetLocalizationLanguage(true);
        UpdateContent();
    }

    [RelayCommand]
    private void ShowSourceText()
    {
        if (_currentMod == null)
            return;

        _currentMod.SetLocalizationLanguage(false);
        UpdateContent();
    }

    private string FormatDependencies(List<SVL.Core.Stardew.Mod.ModDependency>? dependencies)
    {
        if (dependencies == null || dependencies.Count == 0)
            return "无";

        return string.Join("\n", dependencies.Select(d =>
            $"• {d.UniqueId}{(d.IsRequired ? " (必需)" : " (可选)")} {(d.MinimumVersion != null ? $"最低版本: {d.MinimumVersion}" : "")}"));
    }

    private string FormatConflicts(List<string>? conflicts)
    {
        if (conflicts == null || conflicts.Count == 0)
            return "无";

        return string.Join("\n", conflicts.Select(c => $"• {c}"));
    }
}
