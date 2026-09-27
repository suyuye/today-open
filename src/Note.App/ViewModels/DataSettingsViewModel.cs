using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Win32;

using NoteApp.Models;
using NoteApp.Services;
using NoteApp.Views;

namespace NoteApp.ViewModels;

/// <summary>数据设置页 ViewModel：自动备份、备份列表管理、导出 JSON、清空回收站、回收站保留天数、附件存储管理。</summary>
public partial class DataSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IBackupService _backupService;
    private readonly INoteService _noteService;
    private readonly IImportService _importService;
    private readonly IAttachmentService _attachmentService;

    public DataSettingsViewModel(
        ISettingsService settingsService,
        IBackupService backupService,
        INoteService noteService,
        IImportService importService,
        IAttachmentService attachmentService)
    {
        _settingsService = settingsService;
        _backupService = backupService;
        _noteService = noteService;
        _importService = importService;
        _attachmentService = attachmentService;

        _backupEnabled = settingsService.GetBool("data.backup_enabled", true);
        _backupIntervalDays = settingsService.GetInt("data.backup_interval_days", 7);
        _retainDays = settingsService.GetInt("data.retain_days", 30);

        RefreshBackupList();
        RefreshAttachmentSummary();
    }

    /// <summary>备份文件列表（按创建时间倒序，来自 IBackupService）。</summary>
    public ObservableCollection<BackupInfo> BackupList { get; } = new();

    /// <summary>是否有备份文件（控制空状态提示显隐）。</summary>
    public bool HasBackups => BackupList.Count > 0;

    [ObservableProperty]
    private bool _backupEnabled;

    [ObservableProperty]
    private int _backupIntervalDays;

    [ObservableProperty]
    private int _retainDays;

    partial void OnBackupEnabledChanged(bool value) =>
        _settingsService.SetBool("data.backup_enabled", value);

    partial void OnBackupIntervalDaysChanged(int value) =>
        _settingsService.SetInt("data.backup_interval_days", value);

    partial void OnRetainDaysChanged(int value) =>
        _settingsService.SetInt("data.retain_days", value);

    // ===== 命令 =====

    /// <summary>立即备份一次，刷新列表并提示。</summary>
    [RelayCommand]
    private void BackupNow()
    {
        _backupService.BackupNow();
        RefreshBackupList();
        ToastWindow.Show("备份完成");
    }

    /// <summary>
    /// 从指定备份恢复。
    ///
    /// 两步保护：
    /// 1. 先校验备份文件本身可用（完整性检查 + 确认含便签表），
    ///    避免用坏文件覆盖好数据 —— 那一步是不可逆的。
    /// 2. 真正的替换交给 App 在退出流程末尾执行，随后自动重启。
    ///    不能在点击时直接换文件：那时内存里还是旧数据，
    ///    窗口关闭时的自动保存会把旧内容写回，静默覆盖掉恢复结果。
    /// </summary>
    [RelayCommand]
    private void RestoreBackup(string backupPath)
    {
        if (!_backupService.IsValidBackup(backupPath, out var error))
        {
            MessageBox.Show(
                $"该备份文件无法用于恢复，操作已中止。\n\n原因：{error}",
                "恢复备份",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var result = MessageBox.Show(
            "确定要从该备份恢复吗？\n\n" +
            "· 当前数据将被该备份覆盖（覆盖前会自动留存一份当前数据库）\n" +
            "· 恢复需要重启应用才能生效，确认后应用会自动关闭并重新打开",
            "恢复备份",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        if (Application.Current is App app)
        {
            app.RequestRestoreAndRestart(backupPath);
        }
    }

    /// <summary>导出所有便签（含归档，不含回收站）为 JSON 文件。</summary>
    [RelayCommand]
    private void ExportJson()
    {
        var notes = _noteService.GetAllNotes(includeArchived: true);

        var dialog = new SaveFileDialog
        {
            Title = "导出便签为 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"今日营业中_便签_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var json = JsonSerializer.Serialize(notes, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });

        File.WriteAllText(dialog.FileName, json);
        ToastWindow.Show("导出完成");
    }

    /// <summary>
    /// 从本应用导出的 JSON 文件导入便签（可往返）。
    /// 相同 Id 的便签会被跳过，不会覆盖现有数据。
    /// </summary>
    [RelayCommand]
    private void ImportJson()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入便签（JSON）",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = _importService.ImportFromJson(dialog.FileName, VirtualScreenBounds());
        ShowImportResult(result);
    }

    /// <summary>
    /// 从 Markdown / 纯文本文件导入，每个文件生成一条便签。
    /// 文件首行若是 Markdown 一级标题（# 开头）会作为便签标题，否则用文件名。
    /// </summary>
    [RelayCommand]
    private void ImportTextFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入便签（可多选，每个文件一条）",
            Filter = "Markdown / 文本 (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var result = _importService.ImportFromTextFiles(
            dialog.FileNames, _settingsService.DefaultColor, VirtualScreenBounds());
        ShowImportResult(result);
    }

    /// <summary>
    /// 可用的屏幕范围（含多显示器的**虚拟屏**）。
    /// 导入的便签按这个范围摆放，避免落在不存在的显示器上导致「看不到、也拖不回来」。
    /// </summary>
    private static Rect VirtualScreenBounds() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>如实汇报导入结果；有失败项时一并列出，不要只说「导入完成」。</summary>
    private static void ShowImportResult(ImportResult result)
    {
        if (result.HasFailures)
        {
            var details = string.Join("\n", result.Failures.Take(5));
            if (result.Failures.Count > 5)
            {
                details += $"\n…另有 {result.Failures.Count - 5} 项";
            }

            MessageBox.Show(
                $"{result.Summary}\n\n失败详情：\n{details}",
                "导入便签",
                MessageBoxButton.OK,
                result.Imported > 0 ? MessageBoxImage.Warning : MessageBoxImage.Error);
            return;
        }

        ToastWindow.Show(result.Summary);
    }

    /// <summary>清空回收站（确认弹窗后永久删除回收站内所有便签）。</summary>
    [RelayCommand]
    private void ClearTrash()
    {
        var result = MessageBox.Show(
            "确定要永久删除回收站中的所有便签吗？此操作不可撤销。",
            "清空回收站",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _noteService.EmptyRecycleBin();
        ToastWindow.Show("回收站已清空");
    }

    /// <summary>用系统资源管理器打开备份文件夹。</summary>
    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_backupService.BackupDirectory);
        Process.Start("explorer.exe", _backupService.BackupDirectory);
    }

    /// <summary>重新读取备份列表并刷新空状态。</summary>
    public void RefreshBackupList()
    {
        BackupList.Clear();
        foreach (var backup in _backupService.GetBackups())
        {
            BackupList.Add(backup);
        }

        OnPropertyChanged(nameof(HasBackups));
    }

    // ===== 附件管理 =====
    //
    // 背景：图片附件存数据库 BLOB（见 AttachmentService 的设计取舍），
    // 但「永久删除便签」的路径从不清理附件表 —— 便签没了，图片 BLOB 还在，
    // 库文件和每份备份都被这些"看不见的字节"撑大。这里给用户一个入口看到并清掉它们。

    /// <summary>附件存储统计（打开设置页时读取，清理后刷新）。</summary>
    [ObservableProperty]
    private AttachmentStorageSummary _attachmentSummary = new();

    /// <summary>总占用的一句话描述，如「12 张图片 · 共 3.4 MB」。</summary>
    public string AttachmentTotalText =>
        $"{$"{AttachmentSummary.TotalCount} 张图片"} · 共 {FormatBytes(AttachmentSummary.TotalBytes)}";

    /// <summary>孤儿占用的一句话描述，如「2 张图片已失去关联 · 占 1.1 MB」。</summary>
    public string AttachmentOrphanText =>
        $"{AttachmentSummary.OrphanCount} 张图片已失去关联 · 占 {FormatBytes(AttachmentSummary.OrphanBytes)}";

    /// <summary>是否有孤儿附件（控制「清理」按钮显隐）。</summary>
    public bool HasOrphanAttachments => AttachmentSummary.HasOrphans;

    /// <summary>重新统计附件存储占用，并广播相关属性。</summary>
    public void RefreshAttachmentSummary()
    {
        AttachmentSummary = _attachmentService.GetStorageSummary();
        OnPropertyChanged(nameof(AttachmentTotalText));
        OnPropertyChanged(nameof(AttachmentOrphanText));
        OnPropertyChanged(nameof(HasOrphanAttachments));
    }

    /// <summary>清理孤儿附件：确认后删除，刷新统计并提示结果。</summary>
    [RelayCommand]
    private void CleanOrphanAttachments()
    {
        if (!AttachmentSummary.HasOrphans)
        {
            return;
        }

        var result = MessageBox.Show(
            $"有 {AttachmentSummary.OrphanCount} 张图片所属的便签已被彻底删除，" +
            $"这些图片已无法在应用里看到，但还占着 {FormatBytes(AttachmentSummary.OrphanBytes)} 空间。\n\n" +
            "要清理它们吗？此操作不可撤销。",
            "清理失去关联的图片",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var deleted = _attachmentService.DeleteOrphans();
        RefreshAttachmentSummary();
        ToastWindow.Show($"已清理 {deleted} 张失去关联的图片");
    }

    /// <summary>字节数转可读文本（KB/MB 进位到 1024，与文件管理器习惯一致）。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }
}
