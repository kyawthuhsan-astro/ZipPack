using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ZipPackNet.Cli;

/// <summary>
/// Merges the processes Explorer starts for a multi-selection into one operation.
/// </summary>
/// <remarks>
/// A registry based context menu verb is invoked once <em>per selected item</em>: selecting five files and
/// choosing "Compress with ZipPack" starts five processes with one path each. The first process to arrive
/// becomes the collector and listens on a named pipe; the others hand their path over and exit. Once no new
/// process has arrived for a short while, the collector continues with the complete list.
/// </remarks>
internal static class LaunchBatcher
{
    private static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(600);
    private const int ForwardTimeoutMilliseconds = 2000;

    /// <summary>
    /// Returns every path of the batch when this process is the collector, or <see langword="null"/> when the
    /// paths were handed to an already running collector and this process should exit.
    /// </summary>
    /// <param name="verb">The operation; only processes started for the same verb are merged.</param>
    /// <param name="ownPaths">The paths this process was started with.</param>
    /// <param name="quietPeriod">How long to wait for further processes after the last one arrived.</param>
    public static IReadOnlyList<string>? Collect(string verb, IReadOnlyList<string> ownPaths, TimeSpan? quietPeriod = null)
    {
        // Scoped to the user and the logon session so different users never see each other's selections.
        string name = $"ZipPackNet-{verb}-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";

        using var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out bool isCollector);
        if (!isCollector)
        {
            // If the collector just finished collecting, carry on alone rather than losing the paths.
            return TryForward(name, ownPaths) ? null : ownPaths;
        }

        try
        {
            var paths = new List<string>(ownPaths);
            using var server = new NamedPipeServerStream(
                name, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            while (true)
            {
                using var quiet = new CancellationTokenSource(quietPeriod ?? DefaultQuietPeriod);
                try
                {
                    // Runs before any UI exists, so blocking here cannot dead-lock a synchronization context.
                    server.WaitForConnectionAsync(quiet.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using (var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Length > 0)
                        {
                            paths.Add(line);
                        }
                    }
                }

                server.Disconnect();
            }

            return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool TryForward(string name, IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ForwardTimeoutMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (string path in paths)
            {
                writer.WriteLine(path);
            }

            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
