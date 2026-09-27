namespace NoteApp.Services;

/// <summary>单实例运行服务（命名 Mutex）。</summary>
public interface ISingleInstanceService
{
    /// <summary>是否已有其他实例在运行（本实例为第二个及以后）。</summary>
    bool IsAlreadyRunning { get; }

    /// <summary>激活已有实例的窗口（恢复最小化并置前）。</summary>
    void ActivateExistingInstance();
}
