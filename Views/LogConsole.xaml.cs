using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using UnrealManager.Services;

namespace UnrealManager.Views;

public partial class LogConsole : UserControl
{
    private readonly StringBuilder _buffer = new();
    private readonly Dispatcher _dispatcher;
    private bool _flushQueued;

    public LogConsole()
    {
        InitializeComponent();
        _dispatcher = Dispatcher;
        LogService.Instance.LineLogged += OnLine;
        Unloaded += (_, _) => LogService.Instance.LineLogged -= OnLine;

        var ts = DateTime.Now.ToString("HH:mm:ss");
        Output.Text = $"[{ts}] Source Manager ready.\r\n";
    }

    private void OnLine(string line)
    {
        // Coalesce rapid output (build spam) into batched UI updates.
        lock (_buffer)
        {
            _buffer.Append(line).Append("\r\n");
            if (_flushQueued) return;
            _flushQueued = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private void Flush()
    {
        string pending;
        lock (_buffer)
        {
            pending = _buffer.ToString();
            _buffer.Clear();
            _flushQueued = false;
        }
        if (pending.Length == 0) return;

        Output.AppendText(pending);

        // Cap the buffer so very long builds don't grow unbounded.
        const int maxChars = 400_000;
        if (Output.Text.Length > maxChars)
            Output.Text = Output.Text[^(maxChars / 2)..];

        Output.CaretIndex = Output.Text.Length;
        Scroller.ScrollToEnd();
    }

    private void OnClear(object sender, System.Windows.RoutedEventArgs e) => Output.Clear();
}
