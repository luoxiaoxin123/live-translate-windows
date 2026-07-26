using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveTranslate.Core.Export;

/// <summary>
/// Builds the session Markdown (full source text + full translation, not sentence-aligned —
/// the live transcript stream has no reliable alignment) and saves it to the user's Downloads.
/// </summary>
public static class MarkdownExporter
{
    public static string BuildMarkdown(string sourceText, string translationText, DateTime stoppedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 翻译结果");
        sb.AppendLine();
        sb.AppendLine($"- 停止时间：`{stoppedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}`");
        sb.AppendLine();
        sb.AppendLine("## 原文");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(sourceText) ? "（无）" : sourceText.Trim());
        sb.AppendLine();
        sb.AppendLine("## 译文");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(translationText) ? "（无）" : translationText.Trim());
        return sb.ToString();
    }

    /// <summary>File name like 7月26日-14.30-翻译结果.md — a dot instead of the colon NTFS forbids.</summary>
    public static string FileName(DateTime stoppedAt) =>
        $"{stoppedAt.Month}月{stoppedAt.Day}日-{stoppedAt.ToString("HH.mm", CultureInfo.InvariantCulture)}-翻译结果.md";

    /// <summary>Writes the content into Downloads (unique name on collision); returns the full path.</summary>
    public static string SaveToDownloads(string content, DateTime stoppedAt, string? downloadsDirectory = null)
    {
        var directory = downloadsDirectory ?? GetDownloadsPath();
        Directory.CreateDirectory(directory);

        var path = UniquePath(directory, FileName(stoppedAt));
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path);
        return path;
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    public static string GetDownloadsPath()
    {
        try
        {
            var downloadsFolderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            var hr = SHGetKnownFolderPath(ref downloadsFolderId, 0, IntPtr.Zero, out var pathPtr);
            if (hr == 0 && pathPtr != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
