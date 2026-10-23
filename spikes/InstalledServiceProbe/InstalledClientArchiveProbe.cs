using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts;
using PeerSync.Control.Contracts.V1;

namespace PeerSync.InstalledProbe;

/// <summary>Runs the installed App in a separate process and drives its actual
/// WPF controls through Windows UI Automation against the fixed SCM pipe.</summary>
internal static class InstalledClientArchiveProbe
{
    internal static async Task<object> RunAsync(string executable)
    {
        executable = Path.GetFullPath(executable);
        var appFile = new FileInfo(executable);
        if (!appFile.Exists || (appFile.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The client executable must be an ordinary existing file.");
        if (Process.GetProcessesByName("PeerSync").Any(process => !process.HasExited))
            throw new InvalidOperationException("Close the existing PeerSync App before the installed-client probe.");

        string root = Path.Combine(@"C:\PeerSync-Test", "archive-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "notes.txt");
        string copy = Path.Combine(root, "restored-copy.txt");
        await File.WriteAllTextAsync(original, "before").ConfigureAwait(false);
        await FolderAccessGrant.GrantAsync(root, null, CancellationToken.None).ConfigureAwait(false);

        using var client = ControlStatusClient.CreateForInstalledService();
        FolderInfo? folder = null;
        Process? process = null;
        object? outcome = null;
        bool folderRemoved = true;
        try
        {
            folder = await client.AddFolderAsync(root).ConfigureAwait(false);
            Stage("folder-added");
            await UntilAsync(async () =>
            {
                await foreach (IndexEntry row in client.DumpIndexAsync(folder.FolderId))
                    if (row.Path == "notes.txt" && !row.Deleted) return true;
                return false;
            }).ConfigureAwait(false);
            await File.WriteAllTextAsync(original, "after").ConfigureAwait(false);
            await UntilAsync(async () =>
                (await client.ListArchiveAsync(folder.FolderId).ConfigureAwait(false)).Versions
                    .Any(row => row.OriginalPath == "notes.txt")).ConfigureAwait(false);
            Stage("archive-ready");
            string exportedKey = (await client.GetFolderKeysAsync(folder.FolderId).ConfigureAwait(false)).Key;
            bool indexed = false;
            await foreach (IndexEntry entry in client.DumpIndexAsync(folder.FolderId))
                indexed |= entry.Path == "notes.txt" && !entry.Deleted;
            if (!indexed) throw new IOException("The installed index omitted the probe file.");

            process = Process.Start(new ProcessStartInfo(executable)
            { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("Could not start the client.");
            AutomationElement? window = null;
            await UntilAsync(() =>
            {
                process.Refresh();
                if (process.HasExited) throw new IOException("The client exited before showing its window.");
                if (process.MainWindowHandle == 0) return Task.FromResult(false);
                window = AutomationElement.FromHandle(process.MainWindowHandle);
                return Task.FromResult(window is not null);
            }).ConfigureAwait(false);
            if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A different client process was launched.");
            AutomationElement main = window!;
            await UntilAsync(() => Task.FromResult(FindById(main, "ServiceBanner")?.Current.Name
                == "Connected to PeerSync service")).ConfigureAwait(false);
            Stage("client-connected");

            AutomationElement list = FindById(main, "FoldersList")
                ?? throw new IOException("The installed Folders list is missing.");
            AutomationElement? folderItem = null;
            await UntilAsync(() =>
            {
                folderItem = list.FindAll(TreeScope.Children, Condition.TrueCondition)
                    .Cast<AutomationElement>().FirstOrDefault(item =>
                        item.Current.ControlType == ControlType.ListItem
                        && item.Current.Name.Contains(root, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(folderItem is not null);
            }).ConfigureAwait(false);
            Select(folderItem!);
            AutomationElement archiveTab = main.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Archive"))
                .Cast<AutomationElement>().Single(item => item.Current.ControlType == ControlType.TabItem);
            Select(archiveTab);

            AutomationElement grid = FindById(main, "ArchivedVersions")
                ?? throw new IOException("The installed Archive grid is missing.");
            AutomationElement? row = null;
            await UntilAsync(() =>
            {
                row = FindArchiveRow(grid);
                return Task.FromResult(row is not null);
            }).ConfigureAwait(false);
            Select(row!);
            InvokeButton(main, "Restore");
            await UntilAsync(async () => File.Exists(original)
                && await File.ReadAllTextAsync(original).ConfigureAwait(false) == "before").ConfigureAwait(false);
            Stage("original-restored");

            await UntilAsync(() =>
            {
                row = FindArchiveRow(grid);
                return Task.FromResult(row is not null);
            }).ConfigureAwait(false);
            Select(row!);
            InvokeButton(main, "Restore as copy");
            AutomationElement? prompt = null;
            await UntilAsync(() =>
            {
                prompt = FindPromptWindow(process.Id);
                return Task.FromResult(prompt is not null);
            }).ConfigureAwait(false);
            Stage("copy-prompt");
            AutomationElement input = prompt!.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().Single();
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("restored-copy.txt");
            InvokeButton(prompt, "Continue");
            await UntilAsync(async () => File.Exists(copy)
                && await File.ReadAllTextAsync(copy).ConfigureAwait(false) == "before").ConfigureAwait(false);
            Stage("copy-restored");
            int auditedEvents = AuditRawServiceLog(exportedKey, folder.FolderId, root, "notes.txt");
            Stage("service-log-audited");
            outcome = new { appSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
                uiAutomation = true, originalRestored = true, copyRestored = true,
                serviceLogAuditedEvents = auditedEvents, retainedRoot = root };
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                process.Dispose();
            }
            if (folder is not null)
                folderRemoved = (await client.RemoveFolderAsync(folder.FolderId).ConfigureAwait(false)).Completed;
        }
        if (!folderRemoved) throw new IOException("The temporary folder registration was not removed.");
        return outcome!;
    }

    private static AutomationElement? FindById(AutomationElement root, string id) => root.FindFirst(
        TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static void Stage(string stage) => Console.WriteLine(JsonSerializer.Serialize(
        new { target = "installed Archive client", stage }));

    private static int AuditRawServiceLog(string key, string folderId, string root, string fileName)
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PeerSync", "logs", "service");
        string run = Directory.EnumerateDirectories(logs)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            .OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault()
            ?? throw new IOException("The installed service has no raw diagnostic run.");
        if ((File.GetAttributes(logs) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(run) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installed service log path is linked.");
        string[] files = Directory.GetFiles(run, "debug*.jsonl", SearchOption.TopDirectoryOnly);
        if (files.Length == 0) throw new IOException("The installed service has no raw debug journal.");
        int events = 0; bool audit = false;
        foreach (string path in files)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installed raw journal is linked.");
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan));
            while (reader.ReadLine() is { } line)
            {
                events++;
                audit |= line.Contains("control.folder_keys_read", StringComparison.Ordinal);
                if (new[] { key, folderId, root, fileName }.Any(value =>
                    line.Contains(value, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The installed raw service journal exposed folder data.");
            }
        }
        if (!audit) throw new IOException("The installed raw journal omitted the key-read audit event.");
        return events;
    }

    private static AutomationElement? FindArchiveRow(AutomationElement grid) => grid.FindAll(
            TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))
        .Cast<AutomationElement>().FirstOrDefault(row =>
            row.Current.Name.Contains("notes.txt", StringComparison.Ordinal));

    private static void Select(AutomationElement item) =>
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    private static void InvokeButton(AutomationElement root, string name)
    {
        AutomationElement button = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name))
            .Cast<AutomationElement>().Single(item => item.Current.ControlType == ControlType.Button);
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, [Out] char[] text, int maximum);

    private static AutomationElement? FindPromptWindow(int processId)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out uint owner) == 0 || owner != processId) return true;
            var name = new char[128];
            int length = GetWindowText(window, name, name.Length);
            if (length <= 0 || new string(name, 0, length) != "Restore as copy") return true;
            found = window;
            return false;
        }, 0);
        return found == 0 ? null : AutomationElement.FromHandle(found);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!deadline.IsCancellationRequested)
        {
            try { if (await condition().ConfigureAwait(false)) return; }
            catch (ElementNotAvailableException) { }
            try { await Task.Delay(250, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
        }
        throw new TimeoutException("The installed Archive client did not reach the expected state.");
    }
}
