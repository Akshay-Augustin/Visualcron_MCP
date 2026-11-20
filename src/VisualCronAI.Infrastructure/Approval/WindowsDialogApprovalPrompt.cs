using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VisualCronAI.Core.Configuration;
using VisualCronAI.Core.Interfaces;
using VisualCronAI.Core.Models;

namespace VisualCronAI.Infrastructure.Approval;

/// <summary>
/// Fallback human-approval channel: a native Windows Yes/No dialog shown on the interactive desktop of the user
/// running the MCP server. The AI model cannot click it. Production changes require a second, explicit confirmation.
/// </summary>
public sealed partial class WindowsDialogApprovalPrompt(IOptions<VisualCronAIOptions> options, ILogger<WindowsDialogApprovalPrompt> logger) : IApprovalPrompt
{
    public string Channel => "WindowsDialog";

    public bool IsAvailable =>
        OperatingSystem.IsWindows() && Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0;

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Deny("Windows dialogs are not available on this OS.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.Approval.DialogTimeoutSeconds, 30, 3600));

        string title = $"VisualCron change approval — {request.Environment} — {request.ProposalId}";
        string text = Truncate(request.Details, 3500) +
                      "\n\nApprove this change? (Yes = approve, No = reject)";
        int first = await ShowAsync(title, text, MB_ICONWARNING, timeout, ct);
        if (first != IDYES) return Deny(first == IDNO ? "Rejected in Windows dialog." : "Dialog closed or timed out.");

        if (request.IsProduction)
        {
            string prodTitle = $"PRODUCTION CHANGE — {request.ProposalId}";
            string prodText = $"You are about to change the PRODUCTION VisualCron job '{request.JobName}' ({request.Operation}).\n\n" +
                              "This is the second, explicit production confirmation.\n\nProceed with the PRODUCTION change?";
            int second = await ShowAsync(prodTitle, prodText, MB_ICONSTOP, timeout, ct);
            if (second != IDYES) return Deny("Production confirmation declined.");
            return new ApprovalDecision { Approved = true, Channel = Channel, ConfirmationText = request.RequiredConfirmationText, Reason = "Approved in Windows dialog (production confirmed)." };
        }
        return new ApprovalDecision { Approved = true, Channel = Channel, Reason = "Approved in Windows dialog." };
    }

    private ApprovalDecision Deny(string reason) => new() { Approved = false, Channel = Channel, Reason = reason };

    [SupportedOSPlatform("windows")]
    private async Task<int> ShowAsync(string title, string text, uint icon, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                int r = MessageBoxW(IntPtr.Zero, text, title, MB_YESNO | icon | MB_DEFBUTTON2 | MB_TOPMOST | MB_SETFOREGROUND | MB_SYSTEMMODAL);
                tcs.TrySetResult(r);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "ApprovalDialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        if (completed == tcs.Task) return await tcs.Task;

        // Timed out / cancelled: close the dialog so it cannot be approved later.
        IntPtr hwnd = FindWindowW(null, title);
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        logger.LogWarning("Approval dialog timed out or was cancelled.");
        return 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n… (truncated; see visualcron_preview_changes)";

    private const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, MB_ICONSTOP = 0x10, MB_DEFBUTTON2 = 0x100,
        MB_SYSTEMMODAL = 0x1000, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    private const int IDYES = 6, IDNO = 7;
    private const uint WM_CLOSE = 0x0010;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
