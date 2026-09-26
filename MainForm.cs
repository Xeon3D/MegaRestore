namespace MegaRestore;

public sealed class MainForm : Form
{
    readonly TextBox _iso = new() { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly TextBox _out = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Top, Width = 330 };
    readonly Button _pickIso = new() { Text = "Select ISO…", Width = 110 };
    readonly Button _pickOut = new() { Text = "Save as…", Width = 110 };
    readonly Button _start = new() { Text = "Start", Width = 100 };
    readonly Button _quit = new() { Text = "Quit", Width = 100 };
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 22 };
    readonly Label _status = new() { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Text = "Select the first restore disc." };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    string[] _picked = Array.Empty<string>();
    CancellationTokenSource? _cts;
    Task? _job;

    public static readonly (string Label, long Cyl)[] Sizes =
    {
        ("6.4 GB  Quantum Fireball ST6.4A (12495 cyl)", 12495),
        ("4.3 GB  Quantum Fireball 4.3 (8352 cyl)", 8352),
    };

    public MainForm()
    {
        Text = "Megatouch Restore — ISO to hard disk image";
        ClientSize = new Size(720, 460);
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        foreach (var s in Sizes) _size.Items.Add(s.Label);
        _size.SelectedIndex = 0;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 3, RowCount = 7 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 0) };
        grid.Controls.Add(L("Restore disc:"), 0, 0); grid.Controls.Add(_iso, 1, 0); grid.Controls.Add(_pickIso, 2, 0);
        grid.Controls.Add(L("Disk image:"), 0, 1); grid.Controls.Add(_out, 1, 1); grid.Controls.Add(_pickOut, 2, 1);
        grid.Controls.Add(L("Drive:"), 0, 2); grid.Controls.Add(_size, 1, 2);
        grid.Controls.Add(_bar, 0, 3); grid.SetColumnSpan(_bar, 3);
        _status.Height = 20;
        grid.Controls.Add(_status, 0, 4); grid.SetColumnSpan(_status, 3);
        grid.Controls.Add(_log, 0, 5); grid.SetColumnSpan(_log, 3);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_quit); buttons.Controls.Add(_start);
        grid.Controls.Add(buttons, 0, 6); grid.SetColumnSpan(buttons, 3);
        Controls.Add(grid);
        AcceptButton = _start;

        _pickIso.Click += (_, _) => PickIso();
        _pickOut.Click += (_, _) => PickOut();
        _start.Click += async (_, _) => await StartAsync();
        _quit.Click += (_, _) => Close();
        FormClosing += OnClosing;
    }

    void PickIso()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select the Megatouch restore disc (disc 1; the other discs are found in the same folder)",
            Filter = "CD images (*.iso)|*.iso|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _picked = dlg.FileNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        _iso.Text = string.Join("; ", _picked.Select(Path.GetFileName));
        _log.Clear();
        try
        {
            Log("Selected: " + string.Join(", ", _picked));
            using var set = DiscSet.Open(_picked, Log);
            Log($"{set.Title}: {set.Discs.Count} disc(s), {Describe(set.Kind)}");
            _status.Text = set.Kind is DiscKind.D2000Kit or DiscKind.LinuxPartimage ? "Ready." : "This disc cannot be restored by this tool (see log).";
            if (set.Kind is DiscKind.D2000Kit or DiscKind.LinuxPartimage && string.IsNullOrEmpty(_out.Text))
                _out.Text = Path.Combine(Path.GetDirectoryName(_picked[0])!, Safe(set.Title) + ".img");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); _status.Text = ex.Message; }
    }

    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

    public static string Describe(DiscKind k) => k switch
    {
        DiscKind.D2000Kit => "DOS restore kit (D2000)",
        DiscKind.UpgradePatch => "upgrade patch disc (.UDF deltas) — it updates an existing install and cannot build a disk from nothing",
        DiscKind.LinuxPartimage => "Linux restore set (partimage images, LILO), MAXX cabinet profile",
        _ => "not a recognised Megatouch restore disc",
    };

    void PickOut()
    {
        using var dlg = new SaveFileDialog { Title = "Save hard disk image as", Filter = "Raw disk image (*.img)|*.img", FileName = _out.Text, OverwritePrompt = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) _out.Text = dlg.FileName;
    }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s + Environment.NewLine);
    }

    async Task StartAsync()
    {
        if (_job != null) return;
        if (_picked.Length == 0) { _status.Text = "Select a restore disc first."; return; }
        if (string.IsNullOrWhiteSpace(_out.Text)) { _status.Text = "Choose where to save the disk image."; return; }
        string outPath = _out.Text;
        if (File.Exists(outPath))
        {
            if (MessageBox.Show(this, $"{outPath}\n\nalready exists. Replace it?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            File.Delete(outPath);
        }
        long sectors = Sizes[_size.SelectedIndex].Cyl * 16 * 63;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var picked = _picked;
        int lastTick = -1;
        void Progress(double f)
        {
            int t = (int)(f * 1000);
            if (t == lastTick) return;
            lastTick = t;
            BeginInvoke(() => { _bar.Value = Math.Clamp(t, 0, 1000); _status.Text = $"Installing… {f:P1}"; });
        }
        var started = DateTime.Now;
        _job = Task.Run(() => Restore.Run(picked, outPath, sectors, Log, Progress, ct), ct);
        try
        {
            await _job;
            _bar.Value = 1000;
            _status.Text = $"Done in {(DateTime.Now - started):mm\\:ss} — {outPath}";
            Log("Finished: " + outPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outPath);
            _status.Text = "Cancelled; the partial image was deleted.";
            Log("Cancelled.");
        }
        catch (Exception ex)
        {
            TryDelete(outPath);
            _status.Text = "Failed: " + ex.Message;
            Log("Error: " + ex.Message);
        }
        finally
        {
            _job = null;
            SetBusy(false);
        }
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    void SetBusy(bool busy)
    {
        _start.Enabled = _pickIso.Enabled = _pickOut.Enabled = _size.Enabled = _out.Enabled = !busy;
        _quit.Text = busy ? "Cancel" : "Quit";
        if (!busy) _bar.Value = _bar.Value;
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_job == null) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "A restore is running. Stop it and delete the partial image?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _cts?.Cancel();
    }
}
