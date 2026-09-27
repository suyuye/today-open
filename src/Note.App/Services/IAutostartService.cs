namespace NoteApp.Services;

/// <summary>开机自启服务（写入当前用户注册表 Run 键）。</summary>
public interface IAutostartService
{
    bool IsEnabled { get; }

    void Enable();
    void Disable();
    void SetEnabled(bool enabled);
}
