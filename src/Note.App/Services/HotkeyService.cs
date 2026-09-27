using System.Windows.Input;
using System.Windows.Interop;

using NoteApp.Helpers;
using NoteApp.Native;

namespace NoteApp.Services;

/// <summary>全局快捷键服务：P/Invoke RegisterHotKey 注册系统级热键，隐藏 HwndSource 窗口接收 WM_HOTKEY。</summary>
public class HotkeyService : IHotkeyService
{
    private readonly Dictionary<string, int> _ids = new();
    private readonly Dictionary<int, HotkeyConfig> _configs = new();
    private readonly ISettingsService _settingsService;

    private HwndSource? _source;
    private int _nextId = 1;

    public HotkeyService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;

    public bool RegisterHotkey(string name, ModifierKeys modifiers, Key key) =>
        RegisterHotkey(new HotkeyConfig { Name = name, Modifiers = modifiers, Key = key });

    public void UnregisterHotkey(string name)
    {
        if (!_ids.TryGetValue(name, out var id))
        {
            return;
        }

        Unregister(id);
        _ids.Remove(name);
        _configs.Remove(id);
        LogHelper.Info($"注销快捷键 {name}");
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids.Values)
        {
            Unregister(id);
        }

        _ids.Clear();
        _configs.Clear();
        LogHelper.Info("注销全部快捷键");
    }

    public bool IsRegistered(string name) => _ids.ContainsKey(name);

    public HotkeyConfig? GetHotkey(string name) =>
        _ids.TryGetValue(name, out var id) && _configs.TryGetValue(id, out var config) ? config : null;

    public List<HotkeyConfig> GetAllHotkeys() =>
        _configs.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

    public void RegisterDefaultHotkeys()
    {
        var defaults = new[]
        {
            new HotkeyConfig { Name = "hotkey.new_note", Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.N, Description = "新建便签" },
            new HotkeyConfig { Name = "hotkey.search", Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.F, Description = "搜索" },
            new HotkeyConfig { Name = "hotkey.toggle_all", Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.H, Description = "显示/隐藏全部" },
        };

        var success = 0;
        foreach (var config in defaults)
        {
            // 优先读取用户设置（跨重启生效）；未设置或解析失败则回退默认值
            var stored = _settingsService.GetString(config.Name);
            if (HotkeyHelper.TryParse(stored, out var modifiers, out var key))
            {
                config.Modifiers = modifiers;
                config.Key = key;
            }

            if (RegisterHotkey(config))
            {
                success++;
            }
        }

        LogHelper.Info($"全局快捷键注册完成，成功 {success}/{defaults.Length}");
    }

    private bool RegisterHotkey(HotkeyConfig config)
    {
        EnsureSource();
        if (_source is null)
        {
            LogHelper.Warning($"快捷键消息窗口未初始化，无法注册 {config.Name}");
            return false;
        }

        if (_ids.TryGetValue(config.Name, out var oldId))
        {
            Unregister(oldId);
            _ids.Remove(config.Name);
            _configs.Remove(oldId);
        }

        var id = _nextId++;
        var modifiers = (uint)config.Modifiers;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(config.Key);

        if (!WindowNativeMethods.RegisterHotKey(_source.Handle, id, modifiers, vk))
        {
            LogHelper.Warning($"快捷键 {config.Name} 注册失败（可能被其他程序占用）");
            return false;
        }

        _ids[config.Name] = id;
        _configs[id] = config;
        LogHelper.Info($"注册快捷键 {config.Name} 成功");
        return true;
    }

    private void Unregister(int id)
    {
        if (_source is not null)
        {
            WindowNativeMethods.UnregisterHotKey(_source.Handle, id);
        }
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("HotkeyMessageWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(HwndHook);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowNativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_configs.TryGetValue(id, out var config))
            {
                LogHelper.Info($"快捷键触发：{config.Name}");
                HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(config.Name));
            }

            handled = true;
        }

        return IntPtr.Zero;
    }
}
