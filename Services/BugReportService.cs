using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace UnrealManager.Services;

public static class BugReportService
{
    // Public issue tracker. The full report is also always copied to the
    // clipboard, so users can paste it anywhere (Gumroad, Discord, email).
    public const string NewIssueUrl = "https://github.com/ToffelsKater/SourceManagerForUnreal/issues/new";

    /// <summary>
    /// Builds a diagnostic report, copies the full version to the clipboard and
    /// opens a pre-filled GitHub issue in the browser.
    /// </summary>
    public static void Open()
    {
        var cfg = ConfigService.Config;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        var header = new StringBuilder();
        header.AppendLine("**What happened?**");
        header.AppendLine("(Describe the problem here - what did you click, what did you expect, what happened instead?)");
        header.AppendLine();
        header.AppendLine("**Diagnostics**");
        header.AppendLine($"- App version: {version}");
        header.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
        header.AppendLine($"- Engine branch: {cfg.GitBranch}");
        header.AppendLine($"- Build target/config: {cfg.BuildTarget} {cfg.BuildConfiguration}");
        header.AppendLine();
        header.AppendLine("_Please review the log below and remove anything you consider private (paths, server names) before submitting._");
        header.AppendLine();

        var log = LogService.Instance.RecentLines();

        // Full report (all captured lines) goes to the clipboard.
        var full = new StringBuilder(header.ToString());
        full.AppendLine("**Recent log**");
        full.AppendLine("```");
        foreach (var line in log) full.AppendLine(line);
        full.AppendLine("```");
        try { System.Windows.Clipboard.SetText(full.ToString()); } catch { /* clipboard can be locked by another app */ }

        // The URL-embedded body must stay well under browser/GitHub URL limits,
        // so it carries only the tail of the log.
        var body = new StringBuilder(header.ToString());
        body.AppendLine("**Recent log** (tail - the full log is in my clipboard, paste on request)");
        body.AppendLine("```");
        foreach (var line in log.TakeLast(20)) body.AppendLine(line);
        body.AppendLine("```");

        var url = NewIssueUrl
                  + "?title=" + Uri.EscapeDataString($"[Bug] (v{version}) ")
                  + "&body=" + Uri.EscapeDataString(body.ToString());

        LogService.Instance.LogHeader("Bug report");
        LogService.Instance.Log("Opening a pre-filled GitHub issue in your browser.");
        LogService.Instance.Log("The full diagnostic report was copied to your clipboard as backup.");

        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
