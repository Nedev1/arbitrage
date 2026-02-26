using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArbitrageX;

public partial class MainWindow : Window
{
    private readonly PipeServer _pipes;
    private readonly RithmicFeed _rithmicFeed;
    private readonly ArbitrageEngine _engine;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _analysisTimer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<FilledMessage> _fillQueue = new();
    private readonly string _debugLogFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "debug.log");

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Priority escalation can fail without elevated permissions.
        }

        _vm = new MainViewModel();
        DataContext = _vm;
        EnvLoader.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"));
        _vm.RithmicUsername = Environment.GetEnvironmentVariable("RITHMIC_USERNAME") ?? "";
        _vm.RithmicPassword = Environment.GetEnvironmentVariable("RITHMIC_PASSWORD") ?? "";
        _vm.RithmicSystem = Environment.GetEnvironmentVariable("RITHMIC_SYSTEM") ?? "Rithmic Paper Trading";
        _vm.RithmicGateway = Environment.GetEnvironmentVariable("RITHMIC_GATEWAY") ?? "Chicago Area";
        _vm.UseRithmic = !string.IsNullOrWhiteSpace(_vm.RithmicUsername);

        _pipes = new PipeServer();
        _pipes.Log += OnPipeLog;
        _pipes.TerminalConnected += OnTerminalConnected;
        _pipes.Start();
        _rithmicFeed = new RithmicFeed(_pipes.Ticks);

        _engine = new ArbitrageEngine(_pipes, _pipes.Fills);
        _engine.Log += OnEngineLog;
        _engine.FillReceived += OnFillReceived;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _analysisTimer.Tick += (_, _) => UpdateAnalysisProgress();

        AsyncLogger.Start(_debugLogFilePath);
        AppendFileLog("=== ArbitrageX started ===");
    }

    private void OnPipeLog(string message)
    {
        AppendFileLog("[PIPE] " + message);
        _logQueue.Enqueue(message);
    }

    private void OnEngineLog(string message)
    {
        AppendFileLog("[ENGINE] " + message);
        _logQueue.Enqueue(message);
    }

    private void OnTerminalConnected(string terminalId)
    {
        AppendFileLog("[UI] Terminal connected: " + terminalId);
        _logQueue.Enqueue($"Adding {terminalId} to list...");
        Dispatcher.BeginInvoke(new Action(() => _vm.AddTerminal(terminalId)));
    }

    private void RefreshUi()
    {
        var terminals = _pipes.GetTerminals();
        _vm.SyncTerminals(terminals);

        // Poll engine stats directly (5 times a second)
        _vm.TicksPerSecond = _engine.CurrentTps.ToString();
        _vm.GapMs = _engine.CurrentGap.ToString("F3");

        int logsToDisplay = 50;
        string? lastLog = null;

        // DRAIN THE ENTIRE QUEUE TO PREVENT MEMORY LEAKS
        while (_logQueue.TryDequeue(out var logMsg))
        {
            if (logsToDisplay > 0)
            {
                if (!_vm.LowCpuMode) _vm.AddLog(logMsg);
                logsToDisplay--;
            }
            lastLog = logMsg;
        }
        if (lastLog != null) _vm.StatusText = lastLog;

        while (_fillQueue.TryDequeue(out var fill))
        {
            _vm.LastFill = $"{fill.TerminalId} {fill.Symbol} {fill.Side} {fill.Lots:F2} @ {fill.Price:F5} Ticket={fill.Ticket}";
        }

        if (_engine.IsEngineCalibrating)
        {
            _vm.TrafficText = "Calibrating";
            _vm.TrafficBrush = Brushes.OrangeRed;
        }
        else if (_engine.TradingEnabled)
        {
            _vm.TrafficText = "Trading";
            _vm.TrafficBrush = Brushes.LimeGreen;
        }
        else
        {
            _vm.TrafficText = "Stopped";
            _vm.TrafficBrush = Brushes.IndianRed;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPrepareConfig(out var config))
        {
            return;
        }

        _engine.UpdateConfig(config);
        _engine.Start();
        if (config.UseRithmic)
        {
            _ = _rithmicFeed.StartAsync(
                config.RithmicUsername ?? string.Empty,
                config.RithmicPassword ?? string.Empty,
                config.RithmicSystem ?? string.Empty,
                config.RithmicGateway ?? string.Empty,
                config.MasterSymbol ?? string.Empty);
        }
        else
        {
            _rithmicFeed.Stop();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _rithmicFeed.Stop();
        _vm.TrafficText = "Stopped";
        _vm.TrafficBrush = Brushes.IndianRed;
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        _engine.KillSwitch();
        _vm.TrafficText = "Killed";
        _vm.TrafficBrush = Brushes.Red;
    }

    private void OnFillReceived(FilledMessage fill)
    {
        _fillQueue.Enqueue(fill);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPrepareConfig(out var config))
        {
            _vm.AnalysisStatus = "Analysis blocked: invalid config.";
            AppendFileLog("[ANALYSIS] Blocked by invalid config.");
            return;
        }

        _engine.UpdateConfig(config);
        _engine.Start();
        var duration = _vm.GetAnalysisDuration();

        _vm.AnalysisStatus = "Analyzing...";
        _vm.AnalysisResult = "";
        AppendFileLog($"[ANALYSIS] Started. Duration={duration.TotalMinutes:F2}min Source={config.SourceTerminal} Target={config.TargetTerminal} Master={config.MasterSymbol} Slave={config.SlaveSymbol}");
        _analysisTimer.Start();

        AnalysisResult result;
        try
        {
            result = await _engine.StartAnalysis(duration);
        }
        catch (Exception ex)
        {
            _analysisTimer.Stop();
            _vm.AnalysisStatus = "Analysis failed.";
            _vm.AnalysisResult = ex.Message;
            AppendFileLog("[ANALYSIS] Failed: " + ex.Message);
            return;
        }

        _analysisTimer.Stop();
        _vm.AnalysisProgress = "";

        var leader = result.AverageGap > 0 ? "Source" : "Target";
        var viable = Math.Abs(result.AverageGap) >= _vm.GetTriggerThreshold() && result.AverageSpread <= _vm.GetMaxSpread() ? "VIABLE" : "RISKY";

        _vm.AnalysisStatus = "Completed";
        _vm.AnalysisResult = $"Avg Gap: {result.AverageGap:F5} | Static Offset: {result.StaticOffset:F5} | Peak Spike: {result.PeakSpike:F5} | Max Gap: {result.MaxGap:F5} | Avg Latency: {result.AverageLatencyMs:F2} ms | Avg Spread (Target): {result.AverageSpread:F5} | Win Rate: {result.WinRate:P1} | Samples: {result.SampleCount}. {leader} is faster. Arbitrage is {viable}.";
        AppendFileLog("[ANALYSIS] " + _vm.AnalysisResult + " Status=" + result.Status);
    }

    private bool TryPrepareConfig(out EngineConfig config)
    {
        config = _vm.ToConfig();

        config.UseRithmic = _vm.UseRithmic;
        config.TargetTerminal = _vm.SelectedTarget?.Trim();
        if (config.UseRithmic)
        {
            config.SourceTerminal = "RITHMIC-FAST";
        }
        else
        {
            config.SourceTerminal = _vm.SelectedSource?.Trim();
        }

        if (string.IsNullOrWhiteSpace(config.TargetTerminal) ||
            (!config.UseRithmic && string.IsNullOrWhiteSpace(config.SourceTerminal)))
        {
            _vm.StatusText = "Select Source and Target terminals.";
            _vm.AddLog("[CONFIG] Missing Source/Target terminal.");
            return false;
        }

        if (string.Equals(config.SourceTerminal, config.TargetTerminal, StringComparison.OrdinalIgnoreCase))
        {
            _vm.StatusText = "Source and Target cannot be the same terminal.";
            _vm.AddLog("[CONFIG] Source and Target are identical. Select two different terminals.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.MasterSymbol) || string.IsNullOrWhiteSpace(config.SlaveSymbol))
        {
            _vm.StatusText = "Set Master and Slave symbols.";
            _vm.AddLog("[CONFIG] Missing Master/Slave symbol.");
            return false;
        }

        _vm.AddLog($"[CONFIG] Source={config.SourceTerminal} Target={config.TargetTerminal} Master={config.MasterSymbol} Slave={config.SlaveSymbol}");
        AppendFileLog($"[CONFIG] Source={config.SourceTerminal} Target={config.TargetTerminal} Master={config.MasterSymbol} Slave={config.SlaveSymbol}");
        return true;
    }

    private void AppendFileLog(string message)
    {
        AsyncLogger.Enqueue(message);
    }

    private void UpdateAnalysisProgress()
    {
        var endUtc = _engine.AnalysisEndUtc;
        if (!_engine.IsAnalyzing || endUtc is null)
        {
            _vm.AnalysisProgress = "";
            return;
        }

        var remaining = endUtc.Value - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        _vm.AnalysisProgress = $"Analyzing... {remaining:mm\\:ss} remaining";
    }
}
