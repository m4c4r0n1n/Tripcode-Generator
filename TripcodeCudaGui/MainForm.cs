using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TripcodeCudaGui;

internal sealed class MainForm : Form
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private readonly TextBox _target = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _charset = new();
    private readonly ComboBox _compute = new();
    private readonly ReadableCheckBox _ignoreCase = new();
    private readonly ReadableCheckBox _stopFirst = new();
    private readonly TrackBar _gpuBlocks = new();
    private readonly TrackBar _cpuWorkers = new();
    private readonly Label _gpuBlocksValue = new();
    private readonly Label _cpuWorkersValue = new();
    private Control? _gpuWorkersField;
    private Control? _cpuWorkersField;
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _historyButton = new();
    private readonly Label _status = new();
    private readonly Label[] _statValues = [new(), new(), new(), new()];
    private readonly DataGridView _results = new();
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 250 };
    private Process? _process;
    private long _tried;
    private long _rate;
    private long _hits;
    private long _omittedHits;
    private readonly Stopwatch _elapsed = new();
    private readonly Queue<(long Timestamp, long Tried)> _rateSamples = new();
    private readonly HashSet<string> _seenPasswords = new(StringComparer.Ordinal);
    private SearchHistoryStore? _historyStore;
    private SearchHistoryEntry? _currentSearch;
    private long _lastHistorySaveTimestamp;
    private bool _historySaveWarningShown;
    private bool _closing;

    public MainForm()
    {
        Text = "Tripcode Generator - CUDA + CPU";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 740);
        Size = new Size(900, 850);
        BackColor = Color.FromArgb(19, 19, 22);
        ForeColor = Color.FromArgb(241, 241, 245);
        Font = new Font("Consolas", 10.5f);
        BuildLayout();
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
        FormClosing += (_, _) =>
        {
            _closing = true;
            _statsTimer.Stop();
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var darkMode = 1;
        if (DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(Handle, 19, ref darkMode, sizeof(int));

        var captionColor = 0x00131316;
        var titleTextColor = 0x00F1F1F5;
        var roundedCorners = DwmwcpRound;
        _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, DwmwaTextColor, ref titleTextColor, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref roundedCorners, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(26)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 350));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        header.Controls.Add(new Label
        {
            Text = "Tripcode Generator",
            Font = new Font("Consolas", 23, FontStyle.Bold),
            ForeColor = ForeColor,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "Native CUDA, CPU workers, or both",
            ForeColor = Color.FromArgb(173, 176, 188),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 35),
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 5
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        StyleInput(_target);
        _target.PlaceholderText = "koifist, trip";
        _target.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_start.Enabled) _ = StartSearchAsync();
        };
        AddField(panel, "Text to find (comma-separated)", _target, 0, 0, 1);
        ConfigureCombo(_mode, ("Anywhere", "any"), ("At the start", "start"), ("At the end", "end"));
        AddField(panel, "Match location", _mode, 1, 0, 1);
        ConfigureCombo(_charset, ("Letters + numbers", "an"), ("Lowercase + numbers", "lower"), ("Letters, numbers, symbols", "sym"));
        AddField(panel, "Password characters", _charset, 0, 1, 1);
        ConfigureCombo(_compute, ("GPU only", "gpu"), ("CPU only", "cpu"), ("GPU + CPU", "both"));
        AddField(panel, "Compute mode", _compute, 1, 1, 1);
        _compute.SelectedIndexChanged += (_, _) => UpdateWorkerVisibility();
        ConfigureSlider(_gpuBlocks, _gpuBlocksValue, 1, 256, 64, 256, " blocks");
        _gpuBlocks.AccessibleName = "CUDA blocks per launch";
        _gpuBlocks.AccessibleDescription = "Change the active number of CUDA blocks per launch while searching.";
        _gpuWorkersField = AddField(panel, "CUDA blocks per launch", CreateSliderControl(_gpuBlocks, _gpuBlocksValue), 0, 2, 1);
        var cpuWorkerCount = Math.Max(1, Environment.ProcessorCount - 1);
        ConfigureSlider(_cpuWorkers, _cpuWorkersValue, 1, Environment.ProcessorCount, cpuWorkerCount, 1, " workers");
        _cpuWorkers.AccessibleName = "CPU worker threads";
        _cpuWorkers.AccessibleDescription = "Change the active CPU worker thread count while searching.";
        _cpuWorkersField = AddField(panel, "CPU worker threads", CreateSliderControl(_cpuWorkers, _cpuWorkersValue), 1, 2, 1);
        _gpuBlocks.ValueChanged += (_, _) =>
            SendRuntimeSetting("GPU_BLOCKS", _gpuBlocks.Value * 256);
        _cpuWorkers.ValueChanged += (_, _) =>
            SendRuntimeSetting("CPU_WORKERS", _cpuWorkers.Value);
        UpdateWorkerVisibility();
        _ignoreCase.Text = "Ignore upper/lower case";
        _ignoreCase.ForeColor = ForeColor;
        _ignoreCase.Font = new Font("Consolas", 10);
        _ignoreCase.BackColor = panel.BackColor;
        _ignoreCase.Dock = DockStyle.Fill;
        panel.Controls.Add(_ignoreCase, 0, 3);
        _stopFirst.Text = "Stop at first hit";
        _stopFirst.ForeColor = ForeColor;
        _stopFirst.Font = new Font("Consolas", 10);
        _stopFirst.BackColor = panel.BackColor;
        _stopFirst.Dock = DockStyle.Fill;
        panel.Controls.Add(_stopFirst, 1, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0), WrapContents = false };
        StyleButton(_start, "Start search", true);
        StyleButton(_stop, "Stop", false);
        _stop.Enabled = false;
        _start.Click += async (_, _) => await StartSearchAsync();
        _stop.Click += (_, _) => StopSearch();
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_stop);
        panel.Controls.Add(buttons, 0, 4);
        panel.SetColumnSpan(buttons, 2);
        buttons.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        root.Controls.Add(panel, 0, 1);

        var progress = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 10, 0, 0) };
        _status.Text = "Ready. Enter a target and start a search.";
        _status.ForeColor = Color.FromArgb(139, 158, 255);
        _status.Font = new Font("Consolas", 10.5f, FontStyle.Bold);
        _status.Dock = DockStyle.Fill;
        progress.Controls.Add(_status, 0, 0);
        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Color.FromArgb(30, 30, 35),
            Padding = new Padding(14, 8, 14, 7)
        };
        for (var i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var statNames = new[] { "Tried", "Speed", "Found", "Est. time to hit" };
        for (var i = 0; i < statNames.Length; i++)
        {
            stats.Controls.Add(new Label
            {
                Text = statNames[i],
                ForeColor = Color.FromArgb(173, 176, 188),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                Font = new Font("Consolas", 9.5f)
            }, i, 0);
            _statValues[i].Text = i == 1 ? "0/sec" : i == 3 ? "-" : "0";
            _statValues[i].ForeColor = i == 3 ? Color.FromArgb(139, 158, 255) : ForeColor;
            _statValues[i].Font = new Font("Consolas", 14, FontStyle.Bold);
            _statValues[i].Dock = DockStyle.Fill;
            stats.Controls.Add(_statValues[i], i, 1);
        }
        progress.Controls.Add(stats, 0, 1);
        root.Controls.Add(progress, 0, 2);

        var resultsHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        resultsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        resultsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        resultsHeader.Controls.Add(new Label
        {
            Text = "Results   ·   click a password or tripcode to copy",
            ForeColor = ForeColor,
            Font = new Font("Consolas", 12, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        StyleButton(_historyButton, "History", false);
        _historyButton.Anchor = AnchorStyles.Right;
        _historyButton.Click += (_, _) => ShowHistory();
        resultsHeader.Controls.Add(_historyButton, 1, 0);
        root.Controls.Add(resultsHeader, 0, 3);

        _results.Dock = DockStyle.Fill;
        _results.BackgroundColor = Color.FromArgb(30, 30, 35);
        _results.GridColor = Color.FromArgb(54, 54, 62);
        _results.BorderStyle = BorderStyle.None;
        _results.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _results.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _results.EnableHeadersVisualStyles = false;
        _results.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _results.ColumnHeadersHeight = 46;
        _results.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(30, 30, 35),
            ForeColor = Color.FromArgb(173, 176, 188),
            SelectionBackColor = Color.FromArgb(29, 32, 42),
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            Padding = new Padding(9, 2, 9, 2),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _results.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(30, 30, 35),
            ForeColor = Color.FromArgb(241, 241, 245),
            SelectionBackColor = Color.FromArgb(52, 59, 82),
            SelectionForeColor = ForeColor,
            Font = new Font("Consolas", 10.5f),
            Padding = new Padding(9, 4, 9, 4)
        };
        _results.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(34, 34, 40) };
        _results.RowTemplate.Height = 38;
        _results.RowHeadersVisible = false;
        _results.AllowUserToAddRows = false;
        _results.AllowUserToDeleteRows = false;
        _results.AllowUserToResizeRows = false;
        _results.ReadOnly = true;
        _results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _results.MultiSelect = false;
        _results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "password", HeaderText = "Password", FillWeight = 34 });
        _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "tripcode", HeaderText = "Tripcode", FillWeight = 34 });
        _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "matched", HeaderText = "Matched", FillWeight = 32 });
        _results.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is 0 or 1 &&
                _results.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is string value) Copy(value);
        };
        root.Controls.Add(_results, 0, 4);

        Shown += (_, _) =>
        {
            RoundControl(panel, 14);
            RoundControl(stats, 12);
            RoundControl(_results, 12);
            RoundControl(_start, 9);
            RoundControl(_stop, 9);
            RoundControl(_historyButton, 9);
        };
    }

    private static void StyleInput(Control control)
    {
        control.BackColor = Color.FromArgb(20, 20, 23);
        control.ForeColor = Color.FromArgb(241, 241, 245);
        control.Font = new Font("Consolas", 10.5f);
    }

    private static void ConfigureCombo(ComboBox combo, params (string Label, string Value)[] choices)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 24;
        combo.BackColor = Color.FromArgb(20, 20, 23);
        combo.ForeColor = Color.FromArgb(241, 241, 245);
        combo.Font = new Font("Consolas", 10.5f);
        combo.Dock = DockStyle.Fill;
        foreach (var choice in choices) combo.Items.Add(new Option(choice.Label, choice.Value));
        combo.DisplayMember = nameof(Option.Label);
        combo.DrawItem += (_, e) =>
        {
            var option = e.Index >= 0
                ? combo.Items[e.Index] as Option
                : combo.SelectedItem as Option;
            if (option is null) return;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var background = selected && combo.Enabled
                ? Color.FromArgb(52, 59, 82)
                : Color.FromArgb(20, 20, 23);
            var foreground = combo.Enabled
                ? Color.FromArgb(241, 241, 245)
                : Color.FromArgb(176, 179, 191);
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics,
                option.Label,
                combo.Font,
                e.Bounds,
                foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (combo.Enabled) e.DrawFocusRectangle();
        };
        combo.SelectedIndex = 0;
    }

    private static void ConfigureSlider(
        TrackBar slider,
        Label value,
        int minimum,
        int maximum,
        int initial,
        int displayMultiplier = 1,
        string unit = "")
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.Value = Math.Clamp(initial, minimum, maximum);
        slider.TickFrequency = Math.Max(1, (maximum - minimum) / 8);
        slider.SmallChange = 1;
        slider.LargeChange = Math.Max(1, (maximum - minimum) / 8);
        slider.Dock = DockStyle.Fill;
        slider.AutoSize = false;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.ForeColor = Color.FromArgb(241, 241, 245);
        value.Font = new Font("Consolas", 9.5f);
        value.Dock = DockStyle.Right;
        slider.ValueChanged += (_, _) =>
        {
            value.Text = FormatSliderValue(slider.Value, displayMultiplier, unit);
        };
        value.Text = FormatSliderValue(slider.Value, displayMultiplier, unit);
    }

    private static string FormatSliderValue(int value, int multiplier, string unit) =>
        (value * multiplier).ToString("N0", CultureInfo.InvariantCulture) + unit;

    private static Control CreateSliderControl(TrackBar slider, Label value)
    {
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        container.Controls.Add(slider, 0, 0);
        container.Controls.Add(value, 1, 0);
        return container;
    }

    private TableLayoutPanel AddField(TableLayoutPanel parent, string caption, Control control, int column, int row, int span)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 0, 10, 0) };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = Color.FromArgb(186, 188, 198),
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font("Consolas", 9.5f)
        }, 0, 0);
        field.Controls.Add(control, 0, 1);
        parent.Controls.Add(field, column, row);
        if (span > 1) parent.SetColumnSpan(field, span);
        return field;
    }

    private void UpdateWorkerVisibility()
    {
        var compute = (_compute.SelectedItem as Option)?.Value ?? "gpu";
        if (_gpuWorkersField is not null) _gpuWorkersField.Visible = compute is "gpu" or "both";
        if (_cpuWorkersField is not null) _cpuWorkersField.Visible = compute is "cpu" or "both";
    }

    private static void StyleButton(Button button, string caption, bool primary)
    {
        button.Text = caption;
        button.AutoSize = true;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(111, 137, 255) : Color.FromArgb(67, 72, 88);
        button.BackColor = primary ? Color.FromArgb(67, 91, 205) : Color.FromArgb(43, 43, 50);
        button.ForeColor = Color.FromArgb(245, 245, 248);
        button.Font = new Font("Consolas", 10, FontStyle.Bold);
        button.Padding = new Padding(12, 0, 12, 0);
        button.Margin = new Padding(0, 0, 10, 0);
        button.Cursor = Cursors.Hand;
    }

    private static void RoundControl(Control control, int radius)
    {
        void UpdateRegion(object? sender, EventArgs e)
        {
            if (control.Width <= radius * 2 || control.Height <= radius * 2) return;
            using var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(0, 0, diameter, diameter, 180, 90);
            path.AddArc(control.Width - diameter - 1, 0, diameter, diameter, 270, 90);
            path.AddArc(control.Width - diameter - 1, control.Height - diameter - 1, diameter, diameter, 0, 90);
            path.AddArc(0, control.Height - diameter - 1, diameter, diameter, 90, 90);
            path.CloseFigure();
            var oldRegion = control.Region;
            control.Region = new Region(path);
            oldRegion?.Dispose();
        }

        control.Resize += UpdateRegion;
        UpdateRegion(control, EventArgs.Empty);
    }

    private async Task StartSearchAsync()
    {
        var targets = _target.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length is < 1 or > 16)
        {
            ShowError("Enter between 1 and 16 comma-separated targets.");
            return;
        }
        if (targets.Any(value => value.Length > 10 || !Regex.IsMatch(value, @"^[A-Za-z0-9./]+$")))
        {
            ShowError("Targets may contain only tripcode characters (A-Z, a-z, 0-9, . and /), up to 10 characters each.");
            return;
        }

        var mode = ((Option)_mode.SelectedItem!).Value;
        var ignoreCase = _ignoreCase.Checked;
        if (mode == "end" && targets.Any(value =>
                !".26AEIMQUYcgkosw".Contains(value[^1]) &&
                !(ignoreCase && ".26aeimquycgkosw".Contains(value[^1]))))
        {
            ShowError("For an end match, each target must end in one of: .26AEIMQUYcgkosw");
            return;
        }

        var executable = FindCudaExecutable();
        if (executable is null)
        {
            ShowError("TripcodeCuda.exe was not found. Run Build-CUDA.ps1 from the project folder, then reopen this app.");
            return;
        }

        _tried = 0;
        _rate = 0;
        _hits = 0;
        _omittedHits = 0;
        _rateSamples.Clear();
        _rateSamples.Enqueue((Stopwatch.GetTimestamp(), 0));
        _seenPasswords.Clear();
        _results.Rows.Clear();
        _currentSearch = new SearchHistoryEntry
        {
            StartedAt = DateTimeOffset.Now,
            ProcessId = Environment.ProcessId,
            Targets = string.Join(',', targets),
            Mode = mode,
            Charset = ((Option)_charset.SelectedItem!).Value,
            Compute = ((Option)_compute.SelectedItem!).Label,
            Status = "Running"
        };
        try
        {
            _historyStore ??= new SearchHistoryStore();
            _historyStore.Add(_currentSearch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _currentSearch = null;
            ShowError("Could not save search history. The search was not started.\n\n" + exception.Message);
            return;
        }
        _elapsed.Restart();
        _lastHistorySaveTimestamp = Stopwatch.GetTimestamp();
        UpdateStats();
        var compute = ((Option)_compute.SelectedItem!).Value;
        _status.Text = compute == "cpu" ? "Starting CPU workers..." : "Connecting to NVIDIA GPU...";
        _status.ForeColor = Color.FromArgb(111, 137, 255);
        SetSearching(true);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        AddArgument(startInfo, "--target", string.Join(',', targets));
        AddArgument(startInfo, "--mode", mode);
        AddArgument(startInfo, "--charset", ((Option)_charset.SelectedItem!).Value);
        AddArgument(startInfo, "--compute", compute);
        if (compute != "gpu")
            AddArgument(startInfo, "--cpu-workers", _cpuWorkers.Value.ToString(CultureInfo.InvariantCulture));
        if (compute != "cpu")
        {
            var batchSize = _gpuBlocks.Value * 256 * 256;
            AddArgument(startInfo, "--batch", batchSize.ToString(CultureInfo.InvariantCulture));
        }
        if (ignoreCase) startInfo.ArgumentList.Add("--ignore-case");
        if (!_stopFirst.Checked) startInfo.ArgumentList.Add("--continue");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process = process;
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                outputClosed.TrySetResult();
                return;
            }
            if (!_closing && !IsDisposed)
            {
                try
                {
                    Invoke(() => ProcessOutputLine(eventArgs.Data));
                }
                catch (InvalidOperationException) when (_closing || IsDisposed) { }
            }
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows did not start the CUDA search process.");
            process.BeginOutputReadLine();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await outputClosed.Task;
            var error = await errorTask;
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (_currentSearch is not null) _currentSearch.Status = "Failed";
                _status.Text = "Search failed.";
                _status.ForeColor = Color.FromArgb(255, 145, 130);
                ShowError(error.Trim());
            }
            if (_closing) return;

            if (!string.IsNullOrWhiteSpace(error) || process.ExitCode != 0)
            {
                if (_currentSearch is not null) _currentSearch.Status = "Failed";
                _status.Text = "Search failed. Check the error dialog.";
                _status.ForeColor = Color.FromArgb(255, 145, 130);
            }
            else if (_hits > 0 && _stopFirst.Checked)
            {
                _status.Text = "Match found.";
                _status.ForeColor = Color.FromArgb(123, 220, 157);
            }
            else if (_hits > 0)
            {
                _status.Text = $"Search stopped. Found {FormatCount(_hits)} matches.";
                _status.ForeColor = Color.FromArgb(157, 164, 180);
            }
            else
            {
                _status.Text = "Search stopped.";
                _status.ForeColor = Color.FromArgb(157, 164, 180);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_currentSearch is not null) _currentSearch.Status = "Failed";
            if (!_closing)
            {
                _status.Text = "Could not run the search.";
                _status.ForeColor = Color.FromArgb(255, 145, 130);
                ShowError(exception.Message);
            }
        }
        finally
        {
            _elapsed.Stop();
            if (_currentSearch is not null)
            {
                _currentSearch.DurationSeconds = _elapsed.Elapsed.TotalSeconds;
                _currentSearch.CandidatesTried = _tried;
                _currentSearch.HitCount = _hits;
                if (_currentSearch.Status == "Running")
                    _currentSearch.Status = _closing ? "Interrupted" : "Stopped";
                SaveHistoryWithWarning();
                _currentSearch = null;
            }
            if (ReferenceEquals(_process, process)) _process = null;
            if (!_closing) SetSearching(false);
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private void ProcessOutputLine(string line)
    {
        if (line.StartsWith("CONFIG\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t');
            if (fields.Length == 3 &&
                int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var blocks) &&
                int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var cpuWorkers))
            {
                var compute = (_compute.SelectedItem as Option)?.Value ?? "gpu";
                var activeEngines = compute switch
                {
                    "gpu" => $"GPU {blocks:N0} blocks; {blocks * 256L:N0} candidates/launch; CPU off",
                    "cpu" => $"CPU {cpuWorkers:N0} worker threads; GPU off",
                    _ => $"GPU {blocks:N0} blocks; CPU {cpuWorkers:N0} workers"
                };
                _status.Text = "Updated · " + activeEngines;
                _status.ForeColor = Color.FromArgb(111, 137, 255);
            }
        }
        else if (line.StartsWith("ERROR\t", StringComparison.Ordinal))
        {
            _status.Text = line["ERROR\t".Length..];
            _status.ForeColor = Color.FromArgb(255, 145, 130);
        }
        else if (line.StartsWith("Compute: ", StringComparison.Ordinal))
        {
            _status.Text = FormatComputeStatus(line);
            _status.ForeColor = Color.FromArgb(111, 137, 255);
        }
        else if (line.StartsWith("CUDA device:", StringComparison.Ordinal))
        {
            _status.Text = "Searching on " + line["CUDA device:".Length..].Trim();
            _status.ForeColor = Color.FromArgb(111, 137, 255);
        }
        else if (line.StartsWith("PROGRESS\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 4 &&
                long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tried) &&
                long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var totalHits))
            {
                _tried = tried;
                _hits = totalHits;
                if (_currentSearch is not null)
                {
                    _currentSearch.CandidatesTried = tried;
                    _currentSearch.HitCount = totalHits;
                    if (Stopwatch.GetTimestamp() - _lastHistorySaveTimestamp >= Stopwatch.Frequency * 5L)
                    {
                        SaveHistoryWithWarning();
                        _lastHistorySaveTimestamp = Stopwatch.GetTimestamp();
                    }
                }
                RecordRateSample(tried);
                UpdateStats();
            }
        }
        else if (line.StartsWith("HIT\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t');
            if (fields.Length == 4 &&
                (_currentSearch?.Hits.Count ?? 0) < SearchHistoryStore.MaximumSavedHitsPerSearch &&
                _seenPasswords.Add(fields[1]))
            {
                _currentSearch?.Hits.Add(new SearchHistoryHit
                {
                    Password = fields[1],
                    Tripcode = fields[2],
                    MatchedTarget = fields[3]
                });
                if (_results.Rows.Count < 2000)
                {
                    _results.Rows.Insert(0, fields[1], fields[2], fields[3]);
                    UpdateStats();
                }
            }
        }
        else if (line.StartsWith("OVERFLOW\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t');
            if (fields.Length == 2 &&
                long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var omitted))
            {
                _omittedHits += omitted;
                _status.Text = "Many matches in this batch; some result rows were omitted to keep the app responsive.";
                _status.ForeColor = Color.FromArgb(240, 163, 94);
            }
        }
        else if (line.StartsWith("FINISHED\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 4 &&
                long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tried) &&
                long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                _tried = tried;
                if (fields.Length >= 6 &&
                    long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var totalHits))
                {
                    _hits = totalHits;
                }
                if (_currentSearch is not null)
                {
                    _currentSearch.CandidatesTried = _tried;
                    _currentSearch.HitCount = _hits;
                    _currentSearch.Status = _hits > 0 ? "Hit" : "Stopped";
                }
                RecordRateSample(tried);
                UpdateStats();
            }
        }
    }

    private static string FormatComputeStatus(string line)
    {
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 4 ||
            !parts[0].StartsWith("Compute: ", StringComparison.Ordinal) ||
            !parts[1].StartsWith("CUDA blocks/launch: ", StringComparison.Ordinal) ||
            !parts[2].StartsWith("CUDA threads/launch: ", StringComparison.Ordinal) ||
            !parts[3].StartsWith("CPU workers: ", StringComparison.Ordinal))
        {
            return "Searching · engine configuration could not be read.";
        }

        var compute = parts[0]["Compute: ".Length..];
        var blocksText = parts[1]["CUDA blocks/launch: ".Length..];
        var threadsText = parts[2]["CUDA threads/launch: ".Length..];
        var cpuText = parts[3]["CPU workers: ".Length..];
        var gpuStatus = blocksText == "off"
            ? "GPU off"
            : long.TryParse(blocksText, NumberStyles.None, CultureInfo.InvariantCulture, out var blocks) &&
              long.TryParse(threadsText, NumberStyles.None, CultureInfo.InvariantCulture, out var threads)
                ? $"GPU {FormatCount(blocks)} blocks ({FormatCount(threads)} candidates)"
                : "GPU worker count unavailable";
        var cpuStatus = cpuText == "off" ? "CPU off" : $"CPU {cpuText} workers";
        return $"Searching · {compute} · {gpuStatus} · {cpuStatus}";
    }

    private void UpdateStats()
    {
        UpdateRateFromRecentSamples();
        _statValues[0].Text = FormatCount(_tried);
        _statValues[1].Text = FormatCount(_rate) + "/sec";
        _statValues[2].Text = FormatCount(_hits);
        _statValues[3].Text = EstimateNextHit();
        if (_omittedHits > 0)
        {
            _status.Text = $"Found {FormatCount(_hits)} matches; {FormatCount(_omittedHits)} high-volume results were omitted from the table.";
            _status.ForeColor = Color.FromArgb(240, 163, 94);
        }
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private void RecordRateSample(long tried)
    {
        var now = Stopwatch.GetTimestamp();
        if (_rateSamples.Count > 0 && _rateSamples.Last().Tried == tried) return;
        _rateSamples.Enqueue((now, tried));
        UpdateRateFromRecentSamples();
    }

    private void UpdateRateFromRecentSamples()
    {
        var now = Stopwatch.GetTimestamp();
        var windowTicks = Stopwatch.Frequency * 2L;
        while (_rateSamples.Count > 2 && now - _rateSamples.ElementAt(1).Timestamp >= windowTicks)
            _rateSamples.Dequeue();
        if (_rateSamples.Count < 2) return;
        var first = _rateSamples.Peek();
        var last = _rateSamples.Last();
        var elapsedTicks = last.Timestamp - first.Timestamp;
        _rate = elapsedTicks > 0
            ? (long)((last.Tried - first.Tried) * (double)Stopwatch.Frequency / elapsedTicks)
            : 0;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, (int)Math.Round(seconds))} sec";
        if (seconds < 3600) return $"{Math.Round(seconds / 60).ToString("N0", CultureInfo.InvariantCulture)} min";
        if (seconds < 172_800) return $"{seconds / 3600:0.0} hr";
        return $"{Math.Round(seconds / 86_400).ToString("N0", CultureInfo.InvariantCulture)} days";
    }

    private string EstimateNextHit()
    {
        if (_rate <= 0) return "-";
        var targets = _target.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length == 0) return "-";
        var mode = (_mode.SelectedItem as Option)?.Value ?? "any";
        var probability = 0d;
        foreach (var target in targets)
        {
            if (target.Length is < 1 or > 10) continue;
            var targetProbability = 1d;
            foreach (var character in target)
            {
                var caseMultiplier = _ignoreCase.Checked && char.IsAsciiLetter(character) ? 2d : 1d;
                targetProbability *= caseMultiplier / 64d;
            }
            if (mode == "any") targetProbability *= 11 - target.Length;
            else if (mode == "end") targetProbability *= 4;
            probability += targetProbability;
        }
        return probability > 0 ? FormatDuration(1d / (probability * _rate)) : "-";
    }

    private void StopSearch()
    {
        if (_process is not { HasExited: false } process) return;
        _status.Text = "Stopping search...";
        try
        {
            process.StandardInput.WriteLine("STOP");
            process.StandardInput.Flush();
        }
        catch (InvalidOperationException) { }
        catch (IOException exception)
        {
            _status.Text = "Could not send the stop command: " + exception.Message;
            _status.ForeColor = Color.FromArgb(255, 145, 130);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception killException)
            {
                ShowError("Could not stop the search: " + killException.Message);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            ShowError("Could not stop the search: " + exception.Message);
        }
    }

    private void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value);
    }

    private void ShowHistory()
    {
        try
        {
            _historyStore ??= new SearchHistoryStore();
            using var history = new SearchHistoryForm(_historyStore.Entries);
            if (history.ShowDialog(this) != DialogResult.OK || history.SelectedSearch is not { } entry) return;
            _target.Text = entry.Targets;
            SelectOption(_mode, entry.Mode);
            SelectOption(_charset, entry.Charset);
            SelectOption(_compute, entry.Compute == "GPU + CPU" ? "both" : entry.Compute == "CPU only" ? "cpu" : "gpu");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError("Could not read search history.\n\n" + exception.Message);
        }
    }

    private static void SelectOption(ComboBox combo, string value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is Option option && option.Value == value)
            {
                combo.SelectedIndex = index;
                return;
            }
        }
    }

    private void SaveHistory()
    {
        if (_currentSearch is not null) _historyStore?.Save(_currentSearch);
        _historySaveWarningShown = false;
    }

    private void SaveHistoryWithWarning()
    {
        try
        {
            SaveHistory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (_historySaveWarningShown) return;
            _historySaveWarningShown = true;
            ShowError("Could not update saved search history.\n\n" + exception.Message);
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "Tripcode CUDA", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SetSearching(bool active)
    {
        _start.Enabled = !active;
        _stop.Enabled = active;
        _historyButton.Enabled = !active;
        _target.Enabled = !active;
        _mode.Enabled = !active;
        _charset.Enabled = !active;
        _compute.Enabled = !active;
        _ignoreCase.Enabled = !active;
        _stopFirst.Enabled = !active;
        var compute = (_compute.SelectedItem as Option)?.Value ?? "gpu";
        _gpuBlocks.Enabled = !active || compute != "cpu";
        _cpuWorkers.Enabled = !active || compute != "gpu";
    }

    private void SendRuntimeSetting(string name, int value)
    {
        if (_process is not { HasExited: false } process) return;
        try
        {
            process.StandardInput.WriteLine($"{name} {value.ToString(CultureInfo.InvariantCulture)}");
            process.StandardInput.Flush();
        }
        catch (IOException exception)
        {
            _status.Text = $"Could not apply live {name} setting: {exception.Message}";
            _status.ForeColor = Color.FromArgb(255, 145, 130);
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = $"Could not apply live {name} setting: {exception.Message}";
            _status.ForeColor = Color.FromArgb(255, 145, 130);
        }
    }

    private static string? FindCudaExecutable()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "TripcodeCuda.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record Option(string Label, string Value);

    private sealed class ReadableCheckBox : CheckBox
    {
        public ReadableCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var box = new Rectangle(1, (Height - 16) / 2, 16, 16);
            var borderColor = Enabled ? Color.FromArgb(111, 137, 255) : Color.FromArgb(112, 115, 126);
            using (var background = new SolidBrush(Color.FromArgb(20, 20, 23)))
            using (var border = new Pen(borderColor))
            {
                e.Graphics.FillRectangle(background, box);
                e.Graphics.DrawRectangle(border, box);
            }

            if (Checked)
            {
                using var check = new Pen(Enabled
                    ? Color.FromArgb(241, 241, 245)
                    : Color.FromArgb(176, 179, 191), 2);
                e.Graphics.DrawLines(check, new Point[]
                {
                    new Point(box.Left + 3, box.Top + 8),
                    new Point(box.Left + 6, box.Top + 11),
                    new Point(box.Left + 12, box.Top + 4)
                });
            }

            var textColor = Enabled ? ForeColor : Color.FromArgb(176, 179, 191);
            var textBounds = new Rectangle(23, 0, Math.Max(0, Width - 23), Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
