using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TUFReplayRenderer.Media;

namespace TUFReplayRenderer.Configuration;

internal sealed class OutputDirectoryService : IDisposable
{
    private sealed class Selection
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<string> Task;
    }
    private readonly ConcurrentDictionary<string, Selection> selections = new();
    private readonly object sync = new();
    private bool disposed;
    private readonly string platform;
    internal OutputDirectoryService(string platformName) { platform = platformName; }

    internal object Choose(string initialPath)
    {
        lock (sync) {
            if (disposed) throw new RenderOperationException("render_engine_unavailable", "The renderer was disabled. Enable it and choose the save folder again.");
            if (selections.Values.Any(s => !s.Task.IsCompleted))
                throw new RenderOperationException("folder_picker_busy", "A folder selection window is already open. Finish or cancel that selection first.");
            var selection = new Selection();
            selection.Task = Task.Run(() => Pick(initialPath, selection.Cancellation.Token));
            selections[selection.Id] = selection;
            return new { selectionId = selection.Id, pending = true, outputDirectory = (string)null };
        }
    }
    internal object Status(string id)
    {
        if (id == null || !selections.TryGetValue(id, out Selection selection))
            throw new RenderOperationException("folder_selection_missing", "The folder selection expired. Open the folder picker again.");
        if (!selection.Task.IsCompleted) return new { selectionId = id, pending = true, outputDirectory = (string)null };
        try { return new { selectionId = id, pending = false, outputDirectory = selection.Task.GetAwaiter().GetResult() }; }
        catch (OperationCanceledException) { return new { selectionId = id, pending = false, outputDirectory = (string)null }; }
        catch (Exception error) { return new { selectionId = id, pending = false, outputDirectory = (string)null,
            errorCode = error is RenderOperationException failure ? failure.Code : "folder_picker_failed", errorMessage = error.GetBaseException().Message }; }
    }
    internal object Cancel(string id)
    {
        if (id != null && selections.TryGetValue(id, out Selection selection)) selection.Cancellation.Cancel();
        return new { cancelled = true };
    }

    internal static string ValidateWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new RenderOperationException("output_directory_missing", "Choose a folder for the rendered video.", "outputDirectory");
        string normalized;
        try {
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), path.Length > 2 ? path.Substring(2) : "");
            if (!Path.IsPathRooted(path)) throw new ArgumentException("A full folder path is required.");
            normalized = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) {
            throw new RenderOperationException("output_directory_invalid", "The save folder path is invalid. Choose another folder.", "outputDirectory", error);
        }
        try {
            Directory.CreateDirectory(normalized);
            string probe = Path.Combine(normalized, ".tuf-replay-write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) {}
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
            throw new RenderOperationException("output_directory_unwritable", "The video cannot be saved in this folder. Check its permissions and free space, or choose another folder.", "outputDirectory", error);
        }
        return normalized;
    }

    internal static object Open(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new RenderOperationException("output_directory_not_found", "The save folder no longer exists. Check whether the drive is connected.");
        try {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                using (Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true })) {}
            else {
                string executable = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "/usr/bin/open" : "xdg-open";
                using (Process.Start(new ProcessStartInfo { FileName = executable, Arguments = ExternalProcess.Quote(directory), UseShellExecute = false })) {}
            }
            return new { opened = true };
        }
        catch (Exception error) { throw new RenderOperationException("output_directory_open_failed", "The save folder could not be opened. Open the displayed path in your file manager.", null, error); }
    }

    private async Task<string> Pick(string initialPath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string initial;
        try { initial = Path.GetFullPath(initialPath ?? RendererSettings.DefaultOutputDirectory()); }
        catch { initial = Environment.GetFolderPath(Environment.SpecialFolder.Personal); }
        while (!Directory.Exists(initial) && Directory.GetParent(initial) != null) initial = Directory.GetParent(initial).FullName;
        string executable;
        string[] arguments;
        if (platform == "mac") {
            executable = "/usr/bin/osascript";
            string safe = initial.Replace("\\", "\\\\").Replace("\"", "\\\"");
            arguments = new[] { "-e", "POSIX path of (choose folder with prompt \"Choose the rendered video save folder\" default location POSIX file \"" + safe + "\")" };
        }
        else if (platform == "win") {
            executable = "powershell.exe";
            string script = "Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Choose the rendered video save folder'; $d.SelectedPath = '"
                + initial.Replace("'", "''") + "'; if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Write($d.SelectedPath) }; $d.Dispose()";
            arguments = new[] { "-NoProfile", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) };
        }
        else { executable = "zenity"; arguments = new[] { "--file-selection", "--directory", "--title=Choose the rendered video save folder", "--filename=" + initial + Path.DirectorySeparatorChar }; }
        using var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = executable, Arguments = string.Join(" ", arguments.Select(ExternalProcess.Quote)),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }};
        cancellation.ThrowIfCancellationRequested();
        try { if (!process.Start()) throw new IOException("The folder selection helper did not start."); }
        catch (Exception error) { throw new RenderOperationException("folder_picker_unavailable", "The system folder picker is unavailable. Enter the save folder path directly.", null, error); }
        using (cancellation.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) {} })) {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            string result = (await stdout.ConfigureAwait(false)).Trim(), error = await stderr.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) {
                if (platform != "mac" && process.ExitCode == 1 || error.Contains("-128")) return null;
                throw new RenderOperationException("folder_picker_failed", "The folder picker could not complete. Enter the save folder path directly.");
            }
            return string.IsNullOrWhiteSpace(result) ? null : Path.GetFullPath(result);
        }
    }
    public void Dispose() { lock (sync) { disposed = true; foreach (var selection in selections.Values) selection.Cancellation.Cancel(); } }
}
