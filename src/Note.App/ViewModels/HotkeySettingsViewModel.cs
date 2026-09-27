using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>快捷键设置页 ViewModel：3 组全局快捷键的显示、录制状态与冲突提示。录制按键由窗口 code-behind 捕获后调用 <see cref="ApplyHotkey"/>。</summary>
public partial class HotkeySettingsViewModel : ViewModelBase
{
    public const string NewNoteId = "hotkey.new_note";
    public const string SearchId = "hotkey.search";
    public const string ToggleAllId = "hotkey.toggle_all";

    private static readonly string[] AllIds = { NewNoteId, SearchId, ToggleAllId };

    private readonly ISettingsService _settingsService;
    private readonly IHotkeyService _hotkeyService;

    public HotkeySettingsViewModel(ISettingsService settingsService, IHotkeyService hotkeyService)
    {
        _settingsService = settingsService;
        _hotkeyService = hotkeyService;

        _newNoteHotkey = settingsService.GetString(NewNoteId, "Ctrl+Alt+N");
        _searchHotkey = settingsService.GetString(SearchId, "Ctrl+Alt+F");
        _toggleAllHotkey = settingsService.GetString(ToggleAllId, "Ctrl+Alt+H");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewNoteHotkeyDisplay))]
    private string _newNoteHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchHotkeyDisplay))]
    private string _searchHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleAllHotkeyDisplay))]
    private string _toggleAllHotkey;

    /// <summary>当前正在录制的快捷键 ID；null 表示未在录制。</summary>
    [ObservableProperty]
    private string? _recordingHotkey;

    /// <summary>冲突/占用警告文本；null 表示无警告。</summary>
    [ObservableProperty]
    private string? _conflictMessage;

    public string NewNoteHotkeyDisplay => HotkeyHelper.ToDisplay(NewNoteHotkey);

    public string SearchHotkeyDisplay => HotkeyHelper.ToDisplay(SearchHotkey);

    public string ToggleAllHotkeyDisplay => HotkeyHelper.ToDisplay(ToggleAllHotkey);

    [RelayCommand]
    private void StartRecording(string hotkeyId)
    {
        RecordingHotkey = hotkeyId;
        ConflictMessage = null;
    }

    [RelayCommand]
    public void CancelRecording()
    {
        RecordingHotkey = null;
        ConflictMessage = null;
    }

    /// <summary>应用新快捷键：冲突检测 → 注销旧键 + 注册新键 → 写入设置，失败则回滚。</summary>
    public void ApplyHotkey(string hotkeyId, string newHotkey)
    {
        if (!HotkeyHelper.TryParse(newHotkey, out var newModifiers, out var newKey))
        {
            CancelRecording();
            return;
        }

        // 冲突检测：与其余 2 组快捷键比较，重复则不应用并保持录制
        foreach (var otherId in AllIds)
        {
            if (otherId == hotkeyId)
            {
                continue;
            }

            if (HotkeyHelper.TryParse(GetHotkeyValue(otherId), out var otherModifiers, out var otherKey)
                && otherModifiers == newModifiers && otherKey == newKey)
            {
                ConflictMessage = "该快捷键已被使用";
                return;
            }
        }

        // 不冲突：先注销旧键，再注册新键
        var oldHotkey = GetHotkeyValue(hotkeyId);
        var hasOld = HotkeyHelper.TryParse(oldHotkey, out var oldModifiers, out var oldKey);
        if (hasOld)
        {
            _hotkeyService.UnregisterHotkey(hotkeyId);
        }

        if (_hotkeyService.RegisterHotkey(hotkeyId, newModifiers, newKey))
        {
            _settingsService.SetString(hotkeyId, newHotkey);
            SetHotkeyValue(hotkeyId, newHotkey);
            ConflictMessage = null;
            RecordingHotkey = null;
        }
        else
        {
            // 注册失败（被其他程序占用）：回滚旧键并提示
            if (hasOld)
            {
                _hotkeyService.RegisterHotkey(hotkeyId, oldModifiers, oldKey);
            }

            ConflictMessage = "该快捷键被其他程序占用，请更换";
            RecordingHotkey = null;
        }
    }

    private string GetHotkeyValue(string id) => id switch
    {
        NewNoteId => NewNoteHotkey,
        SearchId => SearchHotkey,
        ToggleAllId => ToggleAllHotkey,
        _ => "",
    };

    private void SetHotkeyValue(string id, string value)
    {
        switch (id)
        {
            case NewNoteId: NewNoteHotkey = value; break;
            case SearchId: SearchHotkey = value; break;
            case ToggleAllId: ToggleAllHotkey = value; break;
        }
    }
}
