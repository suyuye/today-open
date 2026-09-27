using System.Windows;

namespace NoteApp.Services;

/// <summary>便签新建/删除编排服务。</summary>
public interface INoteEditorService
{
    /// <summary>
    /// 新建便签并打开窗口；返回便签 Id。
    /// <paramref name="mousePosition"/> 为**屏幕物理像素**坐标（来自 <c>GetCursorPos</c>），
    /// 为空时用级联位置。
    /// </summary>
    Guid CreateNewNote(Point? mousePosition = null);

    /// <summary>删除便签（软删除带撤销 Toast；永久删除不带）。</summary>
    void DeleteNote(Guid noteId, bool permanent = false);
}
