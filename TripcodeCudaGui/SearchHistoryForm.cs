using System.Globalization;

namespace TripcodeCudaGui;

internal sealed class SearchHistoryForm : Form
{
    private readonly DataGridView _searches = new();
    private readonly DataGridView _hits = new();
    private readonly IReadOnlyList<SearchHistoryEntry> _entries;
    private readonly Button _loadSearch = new();

    public SearchHistoryEntry? SelectedSearch { get; private set; }

    public SearchHistoryForm(IReadOnlyList<SearchHistoryEntry> entries)
    {
        _entries = entries;
        Text = "Tripcode Search History";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(1080, 720);
        BackColor = Color.FromArgb(19, 19, 22);
        ForeColor = Color.FromArgb(241, 241, 245);
        Font = new Font("Consolas", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(layout);

        var title = new Label
        {
            Text = "Past searches · select a search to view its saved hits",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            Font = new Font("Consolas", 12, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(title, 0, 0);

        ConfigureGrid(_searches);
        _searches.Columns.Add(TextColumn("started", "Started", 22));
        _searches.Columns.Add(TextColumn("targets", "Search text", 32));
        _searches.Columns.Add(TextColumn("mode", "Location", 12));
        _searches.Columns.Add(TextColumn("compute", "Compute", 12));
        _searches.Columns.Add(TextColumn("status", "Status", 12));
        _searches.Columns.Add(TextColumn("tried", "Tried", 16));
        _searches.Columns.Add(TextColumn("hits", "Hits", 12));
        foreach (var entry in entries.OrderByDescending(item => item.StartedAt))
        {
            var row = _searches.Rows.Add(
                entry.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Targets,
                entry.Mode,
                entry.Compute,
                entry.Status,
                entry.CandidatesTried.ToString("N0", CultureInfo.InvariantCulture),
                entry.HitCount.ToString("N0", CultureInfo.InvariantCulture));
            _searches.Rows[row].Tag = entry;
        }

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = BackColor
        };
        Shown += (_, _) =>
        {
            split.Panel1MinSize = 150;
            split.Panel2MinSize = 130;
            var maximumDistance = split.Height - split.Panel2MinSize - split.SplitterWidth;
            split.SplitterDistance = Math.Clamp(split.Height / 2, split.Panel1MinSize, maximumDistance);
        };
        split.Panel1.Controls.Add(_searches);
        ConfigureGrid(_hits);
        _hits.Columns.Add(TextColumn("password", "Password", 25));
        _hits.Columns.Add(TextColumn("tripcode", "Tripcode", 35));
        _hits.Columns.Add(TextColumn("matched", "Matched", 40));
        _hits.CellClick += (_, args) =>
        {
            if (args.RowIndex < 0 || args.ColumnIndex is not (0 or 1)) return;
            if (_hits.Rows[args.RowIndex].Cells[args.ColumnIndex].Value is string value && value.Length > 0)
                Clipboard.SetText(value);
        };
        split.Panel2.Controls.Add(_hits);
        _searches.SelectionChanged += (_, _) => UpdateHits();
        _searches.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0) LoadSelectedSearch();
        };
        layout.Controls.Add(split, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0)
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        StyleButton(close, false);
        _loadSearch.Text = "Load selected search";
        StyleButton(_loadSearch, true);
        _loadSearch.Click += (_, _) => LoadSelectedSearch();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_loadSearch);
        layout.Controls.Add(buttons, 0, 2);
        CancelButton = close;

        if (_searches.Rows.Count > 0) _searches.Rows[0].Selected = true;
        else
        {
            _loadSearch.Enabled = false;
            _hits.Rows.Add("No saved searches yet.", "", "");
        }
    }

    private void UpdateHits()
    {
        _hits.Rows.Clear();
        if (_searches.CurrentRow?.Tag is not SearchHistoryEntry entry) return;
        foreach (var hit in entry.Hits)
            _hits.Rows.Add(hit.Password, hit.Tripcode, hit.MatchedTarget);
        if (entry.Hits.Count == 0)
            _hits.Rows.Add("No example hits saved for this search.", "", "");
    }

    private void LoadSelectedSearch()
    {
        if (_searches.CurrentRow?.Tag is not SearchHistoryEntry entry) return;
        SelectedSearch = entry;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static DataGridViewTextBoxColumn TextColumn(string name, string header, float fillWeight) =>
        new() { Name = name, HeaderText = header, FillWeight = fillWeight, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.FromArgb(30, 30, 35);
        grid.GridColor = Color.FromArgb(54, 54, 62);
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 40;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(30, 30, 35),
            ForeColor = Color.FromArgb(173, 176, 188),
            SelectionBackColor = Color.FromArgb(30, 30, 35),
            Font = new Font("Consolas", 9, FontStyle.Bold),
            Padding = new Padding(6, 2, 6, 2)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(30, 30, 35),
            ForeColor = Color.FromArgb(241, 241, 245),
            SelectionBackColor = Color.FromArgb(52, 59, 82),
            SelectionForeColor = Color.FromArgb(241, 241, 245),
            Font = new Font("Consolas", 9),
            Padding = new Padding(6, 3, 6, 3)
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(34, 34, 40) };
        grid.RowTemplate.Height = 32;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.AutoSize = true;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(111, 137, 255) : Color.FromArgb(67, 72, 88);
        button.BackColor = primary ? Color.FromArgb(67, 91, 205) : Color.FromArgb(43, 43, 50);
        button.ForeColor = Color.FromArgb(245, 245, 248);
        button.Font = new Font("Consolas", 9, FontStyle.Bold);
        button.Padding = new Padding(10, 0, 10, 0);
        button.Margin = new Padding(8, 0, 0, 0);
    }
}
