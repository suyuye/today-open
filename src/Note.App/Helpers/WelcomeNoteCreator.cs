using System.Windows;

using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.Helpers;

/// <summary>首次启动欢迎便签创建器：以「自动手记人偶」语气介绍软件功能，帮助新用户快速上手。</summary>
public static class WelcomeNoteCreator
{
    private const string FirstRunKey = "general.first_run_completed";

    /// <summary>
    /// 欢迎便签窗口尺寸。
    /// 注意字号：便签默认正文字号是 16.5pt（见 NoteWindowViewModel），
    /// 一个汉字约占 16.5px，340px 宽去掉左右各 12px 内边距后只剩约 19 字/行，
    /// 原文案里那些 20 字以上的说明行会被迫折成两行，整张便签看起来全是断句，
    /// 非常难读。所以欢迎便签单独加宽到 460，让绝大多数说明行都能一行放下。
    /// 高度同时放大约 1.35 倍以容纳加宽后仍存在的换行与全部段落。
    /// </summary>
    private const int NoteWidth = 460;
    private const int NoteHeight = 640;

    /// <summary>
    /// 欢迎便签内容（verbatim 字符串，保留换行）。
    /// 排版原则：段落之间不留空行，只靠「一行标题 + 紧接内容行」的紧凑结构分节。
    /// 便签本身不是文章，空行只会把有限的高度吃掉、把信息推得更散。
    /// </summary>
    private const string WelcomeContent = @"📝 亲爱的客人，欢迎光临「今日营业中」
我是自动手记人偶，将为您记录每一个重要的瞬间。
无论是突发的灵感、待办的事项，还是想珍藏的话语，都请交给我吧。
【本店服务】
• 桌面便签：随时记录，自动保存，无需担心遗失
• 置顶不抢焦点：便签常驻桌面，却不打扰您的工作
• 折叠与锁定：折叠节省空间，锁定防止误触
• 透明度调节：半透明便签温柔融入桌面
• 十二种便签色：六色相各分深浅，随心选择
• 提醒服务：单次/每日/每周/工作日/每月，准时送达
• 标签分类：给信件打上标签，灵活整理归档
• 信件管理面板：搜索、筛选、批量管理所有信件
• 系统托盘：最小化到托盘，不占任务栏空间
• 全局快捷键：一键新建，随时唤出
【快捷咒语】
• Ctrl+Alt+N：召唤新便签
• Ctrl+Alt+H：显示/隐藏全部便签
• Ctrl+Alt+F：搜索信件（如被占用可在设置中修改）
【关于信件管理室】
本店默认只把便签置于您的桌面，不会自动打开信件管理室。
若需查看、搜索或批量管理所有信件，可通过以下方式召唤：
• 系统托盘右键 →「便签列表」
• 全局快捷键 Ctrl+Alt+F（如被占用可在设置中修改）
【使用指南】
• 拖拽标题栏移动便签，拖拽右下角缩放
• 右键标题栏打开更多选项
• 双击折叠栏可编辑便签标题
• 内容自动保存，无需手动操作
【关于离别】
若有一天您决定告别本店，卸载及数据删除步骤
请参考软件目录下的「使用说明.txt」，离别前请记得备份重要的信件。
【关于本店】
创作者：苏羽野
C·H 邮政公司 · 自动手记人偶系统 荣誉出品
本软件为免费软件，无广告、无内购、开源（MIT）
感谢您的光临，愿每一封信都能传达心意。
祝您今天，营业顺利。✨";

    /// <summary>
    /// 首次启动检测：first_run_completed 为 false 且数据库中无任何便签时，创建欢迎便签并置于屏幕中心。
    /// 若已有便签（旧数据），则不创建但标记完成，避免后续重复判断。
    /// </summary>
    public static void CreateIfFirstRun(INoteService noteService, ISettingsService settingsService)
    {
        if (settingsService.GetBool(FirstRunKey, false))
        {
            return;
        }

        if (noteService.GetAllNotes(includeArchived: true).Count > 0)
        {
            settingsService.SetBool(FirstRunKey, true);
            return;
        }

        var note = noteService.CreateNote(title: "欢迎光临「今日营业中」", content: WelcomeContent, color: NoteColor.YellowDeep);

        var workArea = SystemParameters.WorkArea;
        note.PositionX = (int)(workArea.Left + (workArea.Width - NoteWidth) / 2);
        note.PositionY = (int)(workArea.Top + (workArea.Height - NoteHeight) / 2);
        note.Width = NoteWidth;
        note.Height = NoteHeight;
        noteService.UpdateNote(note);

        settingsService.SetBool(FirstRunKey, true);
        LogHelper.Info($"创建欢迎便签 {note.Id}");
    }
}
