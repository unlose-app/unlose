using Unlose.Core.Enums;
using Unlose.Core.Models;
using System.Globalization;
using System.Windows.Data;

namespace Unlose.UI.Converters;

/// <summary>
/// Localization converter for text inside DataTemplates (a DataTemplate cannot use the page's
/// ApplyLanguage, so binding + converter is used instead; the page calls Items.Refresh() in
/// ApplyLanguage to trigger re-evaluation).
/// </summary>
public static class UiText
{
    /// <summary>Snapshot trigger type chip text (emoji + zh/en), consistent with the snapshot management page drawer/filter wording</summary>
    public static string TriggerChip(TriggerType t, bool zh) => t switch
    {
        TriggerType.Scheduled => zh ? "\U0001F4C5 定时快照" : "\U0001F4C5 Scheduled",
        TriggerType.AgentPreSession => zh ? "\U0001F916 Agent启动前" : "\U0001F916 Agent Startup",
        TriggerType.AgentInitiated => zh ? "\U0001F916 Agent主动快照" : "\U0001F916 Agent Snapshot",
        TriggerType.Manual => zh ? "\U0001F590 手动" : "\U0001F590 Manual",
        TriggerType.PreRestore => zh ? "\U0001F4BE 还原前备份" : "\U0001F4BE Pre-Restore",
        _ => t.ToString()
    };

    /// <summary>
    /// Description column text: for pipeline-B AgentInitiated, compose "process: note(channel)"
    /// (parses the persisted "src (ch)" format; legacy "via X" data falls back to "X: note");
    /// without a note only "process(channel)"; other types show TriggerDetail as-is.
    /// </summary>
    public static string SnapshotDescription(SnapshotRecord s, bool zh)
    {
        var detail = s.TriggerDetail ?? string.Empty;
        if (s.TriggerType != TriggerType.AgentInitiated) return detail;

        var label = TranslateLabel(s.Label, zh);
        var m = System.Text.RegularExpressions.Regex.Match(
            detail, @"^(?<src>.+) \((?<ch>cli|mcp|skill)\)$", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(200));
        if (m.Success)
        {
            var src = m.Groups["src"].Value;
            var ch = $"({m.Groups["ch"].Value})";
            return string.IsNullOrWhiteSpace(label) ? $"{src}{ch}" : $"{src}: {label}{ch}";
        }
        if (detail.StartsWith("via ", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(label) ? detail : $"{detail[4..]}: {label}";
        return string.IsNullOrWhiteSpace(label) ? detail : $"{detail}: {label}";
    }

    /// <summary>Multilingual text for preset notes (labels): system-generated notes use the text for the current UI language; user-defined notes are kept as-is.
    /// Keys are compatible with old and new data (older versions carry a "(MCP init)" suffix).</summary>
    private static readonly Dictionary<string, (string Zh, string En)> BuiltinLabels = new()
    {
        ["新会话开始"] = ("新会话开始", "Session start"),
        ["Session start"] = ("新会话开始", "Session start"),
        ["新会话开始 (MCP init)"] = ("新会话开始", "Session start"),
        ["Session start (MCP init)"] = ("新会话开始", "Session start"),
        ["新会话开始 (skill)"] = ("新会话开始", "Session start"),
    };

    private static string TranslateLabel(string? label, bool zh)
    {
        if (string.IsNullOrWhiteSpace(label)) return label ?? string.Empty;
        return BuiltinLabels.TryGetValue(label, out var t) ? (zh ? t.Zh : t.En) : label;
    }

    /// <summary>Text for the pinned state column</summary>
    public static string Pinned(bool isPinned, bool zh)
        => isPinned ? (zh ? "\U0001F512 固定" : "\U0001F512 Pinned") : "—";

    // Historical text → display name (used by ReplaceTriggerTypeNames). Replace in descending length order to avoid substring collateral:
    // covers three generations — English enum names (persisted by the 07-17 deployment), snake_case (earlier monitor module), old Chinese text
    private static readonly (string Raw, string Zh, string En)[] TriggerTypeDisplayNames =
    {
        ("AgentPreSession", "Agent启动前", "Agent Startup"),
        ("agent_pre_session", "Agent启动前", "Agent Startup"),
        ("Agent会话前", "Agent启动前", "Agent Startup"),
        ("AgentInitiated", "Agent主动快照", "Agent Snapshot"),
        ("agent_initiated", "Agent主动快照", "Agent Snapshot"),
        ("Scheduled", "定时快照", "Scheduled"),
        ("PreRestore", "还原前备份", "PreRestore"),
        ("Manual", "手动", "Manual"),
    };

    /// <summary>Replace historical trigger-type spellings in the text with display names (tolerates enum/old-text leakage in legacy database Descriptions)</summary>
    public static string ReplaceTriggerTypeNames(string text, bool zh)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (raw, zhName, enName) in TriggerTypeDisplayNames.OrderByDescending(x => x.Raw.Length))
            text = text.Replace(raw, zh ? zhName : enName);
        return text;
    }
}

/// <summary>TriggerType → localized chip text (shows only the type name; notes go to the description column)</summary>
public sealed class TriggerTypeChipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SnapshotRecord s => UiText.TriggerChip(s.TriggerType, LocalizationService.IsChinese),
        TriggerType t => UiText.TriggerChip(t, LocalizationService.IsChinese),
        _ => value?.ToString() ?? "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SnapshotRecord → description column text (pipeline-B composes "source: note(channel)")</summary>
public sealed class SnapshotDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SnapshotRecord s => UiText.SnapshotDescription(s, LocalizationService.IsChinese),
        _ => value?.ToString() ?? "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool IsPinned → localized state column text</summary>
/// <summary>Snapshot time UTC → local time (DB stores UTC; the UI shows local time, aligned with log timestamps).</summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToLocalTime() : (value ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PinnedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => UiText.Pinned(value is true, LocalizationService.IsChinese);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
