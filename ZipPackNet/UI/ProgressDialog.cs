using System.ComponentModel;
using System.Diagnostics;
using ZipPack;
using ZipPackNet.Shell;

namespace ZipPackNet.UI;

/// <summary>How a background operation ended.</summary>
internal enum OperationStatus
{
    Completed,
    Cancelled,
    Failed,
}

/// <summary>The outcome of an operation run by <see cref="ProgressDialog"/>.</summary>
/// <param name="Status">How the operation ended.</param>
/// <param name="Error">The exception when <paramref name="Status"/> is <see cref="OperationStatus.Failed"/>.</param>
internal sealed record OperationResult(OperationStatus Status, Exception? Error = null);

/// <summary>
/// Runs a long operation on the thread pool while showing its progress, and lets the user cancel it.
/// </summary>
/// <remarks>
/// <para>The UI thread only ever paints: the work runs in <see cref="Task.Run(Func{Task})"/> and is awaited,
/// so the window stays responsive and can be moved, minimised or cancelled at any time.</para>
/// <para>The engines report progress from worker threads, sometimes thousands of times per second. Marshalling
/// each report to the UI thread would flood the message queue, so the reports only overwrite a small "latest
/// value" slot and a timer repaints from it about twenty times per second.</para>
/// </remarks>
[DesignerCategory("Code")]
internal sealed class ProgressDialog : DialogBase
{
    private const int BarResolution = 1000;

    private readonly Func<IProgress<CompressionProgress>, CancellationToken, Task> _work;
    private readonly Func<CompressionProgress, string>? _describe;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LatestValueProgress _progress = new();
    private readonly Stopwatch _elapsed = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };

    private readonly Label _headline;
    private readonly Label _currentItem;
    private readonly Label _details;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;

    private OperationResult _result = new(OperationStatus.Cancelled);
    private bool _finished;
    private TaskbarProgress? _taskbar;

    private ProgressDialog(
        string headline,
        Func<IProgress<CompressionProgress>, CancellationToken, Task> work,
        Func<CompressionProgress, string>? describe)
        : base("ZipPack")
    {
        _work = work;
        _describe = describe;
        ControlBox = true;

        _headline = new Label
        {
            Text = headline,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size + 2.5f),
            Margin = new Padding(0, 0, 0, 8),
        };

        // Fixed width with end-ellipsis: long paths must not resize the dialog while it is running.
        _currentItem = new Label
        {
            Text = "Preparing…",
            AutoSize = false,
            AutoEllipsis = true,
            Size = new Size(460, 20),
            Margin = new Padding(0, 0, 0, 6),
            UseMnemonic = false,
        };

        _bar = new ProgressBar
        {
            Maximum = BarResolution,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            ForeColor = AppTheme.Accent, // Used where the bar is not drawn by the visual style (dark mode).
            Size = new Size(460, 18),
            Margin = new Padding(0, 0, 0, 6),
        };

        _details = new Label
        {
            AutoSize = false,
            Size = new Size(460, 20),
            ForeColor = SystemColors.GrayText,
            Margin = Padding.Empty,
        };

        _cancel = CreateButton("Cancel");
        _cancel.Click += (_, _) => RequestCancel();
        CancelButton = _cancel;

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(_cancel);

        TableLayoutPanel grid = CreateGrid(columns: 1);
        grid.Controls.Add(_headline);
        grid.Controls.Add(_currentItem);
        grid.Controls.Add(_bar);
        grid.Controls.Add(_details);
        grid.Controls.Add(buttons);
        Controls.Add(grid);

        _timer.Tick += (_, _) => RefreshProgress();
    }

    /// <summary>
    /// Shows the dialog, runs <paramref name="work"/> in the background and returns when it has finished,
    /// failed or been cancelled. Never throws for failures of the work itself.
    /// </summary>
    /// <param name="owner">The window to centre on, or <see langword="null"/> when there is no main window.</param>
    /// <param name="headline">What is being done, e.g. "Compressing photos.7z".</param>
    /// <param name="work">The operation. It receives a progress sink and a token that is cancelled by the user.</param>
    /// <param name="describe">
    /// Words the amount done (e.g. "3 of 10 images"); <see langword="null"/> shows it as bytes. The percentage and
    /// elapsed time are added by the dialog.
    /// </param>
    public static OperationResult Run(
        IWin32Window? owner,
        string headline,
        Func<IProgress<CompressionProgress>, CancellationToken, Task> work,
        Func<CompressionProgress, string>? describe = null)
    {
        using var dialog = new ProgressDialog(headline, work, describe);
        dialog.ShowDialog(owner);
        return dialog._result;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The progress belongs on the taskbar button the user sees: the main window's when there is one,
        // otherwise (Explorer launches) this dialog's own button.
        _taskbar = new TaskbarProgress((Owner ?? this).Handle);
        _taskbar.SetState(TaskbarProgressState.Indeterminate);

        _elapsed.Start();
        _timer.Start();

        try
        {
            // Task.Run keeps even the synchronous start-up of an operation (collecting files, loading
            // the native library, deriving keys) off the UI thread.
            await Task.Run(() => _work(_progress, _cancellation.Token));
            _result = new OperationResult(OperationStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            _result = new OperationResult(OperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _result = new OperationResult(OperationStatus.Failed, ex);
        }
        finally
        {
            _timer.Stop();
            _taskbar?.Clear();
            _finished = true;
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window is a cancel request; the dialog closes itself once the work has stopped.
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancel();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _cancellation.Dispose();
            _headline.Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RequestCancel()
    {
        if (_cancel.Enabled)
        {
            _cancel.Enabled = false;
            _cancel.Text = "Cancelling…";
            _taskbar?.SetState(TaskbarProgressState.Paused);
            _cancellation.Cancel();
        }
    }

    private void RefreshProgress()
    {
        string time = Format.Duration(_elapsed.Elapsed);
        if (!_progress.TryGetLatest(out CompressionProgress progress))
        {
            _details.Text = $"Elapsed {time}";
            return;
        }

        if (progress.TotalBytes > 0)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Round(progress.Fraction * BarResolution);
            if (_taskbar is { State: not TaskbarProgressState.Paused })
            {
                _taskbar.SetValue(progress.Fraction);
            }

            string amount = _describe?.Invoke(progress)
                ?? $"{Format.Size(progress.BytesProcessed)} of {Format.Size(progress.TotalBytes)}";
            _details.Text = amount.Length > 0
                ? $"{progress.Percent}%  ·  {amount}  ·  {time}"
                : $"{progress.Percent}%  ·  {time}";
            Text = $"{progress.Percent}% – ZipPack";
        }
        else
        {
            _details.Text = $"{Format.Size(progress.BytesProcessed)}  ·  {time}";
        }

        if (!string.IsNullOrEmpty(progress.CurrentItem))
        {
            _currentItem.Text = progress.CurrentItem;
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> that is safe and cheap to call from any thread at any rate.</summary>
    private sealed class LatestValueProgress : IProgress<CompressionProgress>
    {
        private readonly Lock _gate = new();
        private CompressionProgress _latest;
        private bool _hasValue;

        public void Report(CompressionProgress value)
        {
            lock (_gate)
            {
                _latest = value;
                _hasValue = true;
            }
        }

        public bool TryGetLatest(out CompressionProgress value)
        {
            lock (_gate)
            {
                value = _latest;
                return _hasValue;
            }
        }
    }
}
