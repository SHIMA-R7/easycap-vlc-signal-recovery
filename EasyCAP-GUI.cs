using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed class CaptureForm : Form
{
    readonly string root;
    readonly string settingsPath;
    readonly Button preview = new Button(), record = new Button(), stop = new Button();
    readonly Button browse = new Button(), openFolder = new Button();
    readonly TextBox folder = new TextBox();
    readonly CheckBox color = new CheckBox();
    readonly Label status = new Label(), elapsed = new Label(), fileLabel = new Label(), detail = new Label();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = new Stopwatch();
    readonly StringBuilder output = new StringBuilder();
    readonly object outputLock = new object();
    Process capture;
    string stopFile, statusFile, recordingFile;
    bool stopping, closeWhenFinished, receivedFrames;
    readonly Color ink = Color.FromArgb(30, 41, 59), muted = Color.FromArgb(91, 106, 127);

    public CaptureForm(string directory) : this(directory, null) { }

    internal CaptureForm(string directory, string settingsOverride)
    {
        root = directory;
        settingsPath = settingsOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyCAP", "gui.settings");
        Text = "EasyCAP";
        Font = new Font("Yu Gothic UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(700, 482);
        MinimumSize = new Size(680, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(244, 247, 251);
        Icon = SystemIcons.Application;

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Color.FromArgb(22, 32, 48) };
        header.Controls.Add(new Label { Text = "EasyCAP", ForeColor = Color.White, Font = new Font(Font.FontFamily, 22F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 12) });
        header.Controls.Add(new Label { Text = "プレビューのみ、または録画しながら表示します。", ForeColor = Color.FromArgb(190, 204, 223), AutoSize = true, Location = new Point(27, 56) });

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 14), ColumnCount = 1, RowCount = 8 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 74, 28, 36, 38, 30, 42, 50, 40 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        StyleButton(preview, "プレビュー起動", Color.FromArgb(37, 99, 235));
        StyleButton(record, "録画して起動", Color.FromArgb(202, 49, 68));
        preview.Margin = new Padding(0, 0, 8, 8);
        record.Margin = new Padding(8, 0, 0, 8);
        actions.Controls.Add(preview, 0, 0); actions.Controls.Add(record, 1, 0);
        body.Controls.Add(actions, 0, 0);
        body.Controls.Add(new Label { Text = "録画の保存先", Dock = DockStyle.Fill, ForeColor = ink, TextAlign = ContentAlignment.BottomLeft }, 0, 1);

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        folder.Dock = DockStyle.Fill; folder.Margin = new Padding(0, 3, 8, 0); folder.AccessibleName = "録画の保存先";
        folder.Text = Path.Combine(root, "recordings");
        browse.Text = "変更…"; browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 1, 0, 2);
        pathRow.Controls.Add(folder, 0, 0); pathRow.Controls.Add(browse, 1, 0); body.Controls.Add(pathRow, 0, 2);
        color.Text = "色補正を使う"; color.Checked = true; color.Dock = DockStyle.Fill; color.ForeColor = ink;
        body.Controls.Add(color, 0, 3);

        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        status.Text = "待機中"; status.Dock = DockStyle.Fill; status.Font = new Font(Font, FontStyle.Bold); status.ForeColor = ink;
        elapsed.Text = "00:00:00"; elapsed.Dock = DockStyle.Fill; elapsed.TextAlign = ContentAlignment.TopRight; elapsed.ForeColor = muted;
        statusRow.Controls.Add(status, 0, 0); statusRow.Controls.Add(elapsed, 1, 0); body.Controls.Add(statusRow, 0, 4);
        fileLabel.Text = "録画は日時付きの新しいMKVファイルに保存します。";
        fileLabel.Dock = DockStyle.Fill; fileLabel.AutoEllipsis = true; fileLabel.ForeColor = muted;
        body.Controls.Add(fileLabel, 0, 5);
        detail.Text = "ゲーム機より先に開始すると、起動直後の映像も記録できます。";
        detail.Dock = DockStyle.Fill; detail.ForeColor = muted;
        body.Controls.Add(detail, 0, 6);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        openFolder.Text = "保存先を開く"; openFolder.Dock = DockStyle.Fill; openFolder.Margin = new Padding(0, 0, 12, 0);
        stop.Text = "停止"; stop.Dock = DockStyle.Fill; stop.Enabled = false; stop.Margin = Padding.Empty;
        footer.Controls.Add(openFolder, 0, 0); footer.Controls.Add(stop, 1, 0); body.Controls.Add(footer, 0, 7);
        Controls.Add(body); Controls.Add(header);
        LoadSettings();

        preview.Click += delegate { StartCapture(false); };
        record.Click += delegate { StartCapture(true); };
        stop.Click += delegate { RequestStop(); };
        browse.Click += delegate {
            using (var dialog = new FolderBrowserDialog { Description = "録画の保存先を選んでください", SelectedPath = folder.Text, ShowNewFolderButton = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; SaveSettings(); }
        };
        openFolder.Click += delegate {
            try { string path = Path.GetFullPath(folder.Text.Trim()); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { ShowError(e.Message); }
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (capture != null) { e.Cancel = true; closeWhenFinished = true; RequestStop(); }
        };
        timer.Interval = 250; timer.Tick += PollCapture; timer.Start();
    }

    static void StyleButton(Button button, string text, Color background)
    {
        button.Text = text; button.Dock = DockStyle.Fill; button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0; button.BackColor = background; button.ForeColor = Color.White;
        button.Font = new Font("Yu Gothic UI", 13F, FontStyle.Bold); button.Cursor = Cursors.Hand;
    }
    static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slash = 0;
        foreach (char c in value) {
            if (c == '\\') { slash++; continue; }
            result.Append('\\', c == '"' ? slash * 2 + 1 : slash); result.Append(c); slash = 0;
        }
        result.Append('\\', slash * 2); return result.Append('"').ToString();
    }
    void LoadSettings()
    {
        try { string[] values = File.ReadAllLines(settingsPath); if (values.Length > 0 && !string.IsNullOrWhiteSpace(values[0])) folder.Text = values[0]; if (values.Length > 1) color.Checked = values[1] != "False"; }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)); File.WriteAllLines(settingsPath, new[] { folder.Text, color.Checked.ToString() }); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    void AppendOutput(string text)
    {
        if (text == null) return;
        lock (outputLock) { output.AppendLine(text); if (output.Length > 16000) output.Remove(0, output.Length - 16000); }
    }
    void SetActive(bool active)
    {
        preview.Enabled = record.Enabled = browse.Enabled = folder.Enabled = color.Enabled = !active;
        stop.Enabled = active && !stopping;
    }
    void StartCapture(bool save)
    {
        if (capture != null) return;
        try {
            string script = Path.Combine(root, "Capture-EasyCAP.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Capture-EasyCAP.ps1が見つかりません。EasyCAPフォルダー内で起動してください。");
            recordingFile = null;
            if (save) {
                string path = Path.GetFullPath(folder.Text.Trim()); Directory.CreateDirectory(path); folder.Text = path;
                recordingFile = Path.Combine(path, "EasyCAP-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".mkv");
            }
            string session = Path.Combine(Path.GetTempPath(), "easycap-gui-" + Guid.NewGuid().ToString("N"));
            stopFile = session + ".stop"; statusFile = session + ".status";
            var args = new StringBuilder("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ");
            args.Append(Quote(script)).Append(" -Preview -StopFile ").Append(Quote(stopFile)).Append(" -StatusFile ").Append(Quote(statusFile));
            if (save) args.Append(" -RecordingPath ").Append(Quote(recordingFile));
            if (!color.Checked) args.Append(" -NaturalColor");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), args.ToString()) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            lock (outputLock) output.Clear();
            var child = new Process { StartInfo = info };
            child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { AppendOutput(e.Data); };
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { AppendOutput(e.Data); };
            if (!child.Start()) { child.Dispose(); throw new IOException("キャプチャーを起動できませんでした。"); }
            capture = child; child.BeginOutputReadLine(); child.BeginErrorReadLine();
            stopping = false; closeWhenFinished = false; receivedFrames = false; clock.Restart(); elapsed.Text = "00:00:00"; SaveSettings(); SetActive(true);
            status.Text = "起動しています…"; status.ForeColor = ink;
            fileLabel.Text = save ? "保存ファイル：" + recordingFile : "プレビューのみ。録画ファイルは作成しません。";
            detail.Text = "映像はVLCのウィンドウに表示します。終了は「停止」またはVLCを閉じてください。";
        }
        catch (Exception e) { SetActive(false); ShowError(e.Message); }
    }
    void RequestStop()
    {
        if (capture == null || stopping) return;
        try {
            File.WriteAllText(stopFile, "stop"); stopping = true; stop.Enabled = false;
            status.Text = recordingFile == null ? "停止しています…" : "録画を保存して停止しています…";
            detail.Text = "ファイルの仕上げが終わるまで、そのままお待ちください。";
        }
        catch (Exception e) { closeWhenFinished = false; ShowError("停止の通知に失敗しました。VLCの窓を閉じて終了してください。\n" + e.Message); }
    }
    void PollCapture(object sender, EventArgs e)
    {
        if (capture == null) return;
        var duration = clock.Elapsed;
        elapsed.Text = ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00");
        if (!capture.HasExited) {
            try {
                string state;
                using (var stream = new FileStream(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) state = reader.ReadToEnd().Trim();
                if (!stopping && state == "running") {
                    receivedFrames = true; status.Text = recordingFile == null ? "プレビュー中" : "録画中"; status.ForeColor = recordingFile == null ? Color.FromArgb(22, 110, 70) : Color.FromArgb(190, 40, 60);
                    detail.Text = "映像はVLCに表示しています。「停止」またはVLCを閉じて終了できます。";
                }
                if (!stopping && state == "waiting") { status.Text = "映像入力を待機中"; status.ForeColor = muted; detail.Text = "まだ映像が進んでいません。機器の接続とゲーム機の電源を確認してください。"; }
                if (!stopping && state == "stalled") { status.Text = "映像入力が止まっています"; status.ForeColor = Color.FromArgb(170, 100, 0); detail.Text = "復帰を待っています。戻らない場合は停止後にEasyCAPのUSBを差し直してください。"; }
                if (state == "finalizing") { status.Text = recordingFile == null ? "終了処理中…" : "録画ファイルを仕上げています…"; stop.Enabled = false; }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return;
        }
        capture.WaitForExit(); int code = capture.ExitCode; capture.Dispose(); capture = null; clock.Stop();
        receivedFrames = receivedFrames || File.Exists(statusFile + ".ready");
        string backendError = ""; try { if (File.Exists(statusFile + ".error")) backendError = File.ReadAllText(statusFile + ".error", Encoding.UTF8); } catch (IOException) { }
        DeleteSessionFile(stopFile); DeleteSessionFile(statusFile); DeleteSessionFile(statusFile + ".ready"); DeleteSessionFile(statusFile + ".error"); stopping = false; SetActive(false);
        if (code == 0 && !receivedFrames) {
            status.Text = "映像が届かないまま停止しました"; status.ForeColor = Color.FromArgb(170, 100, 0);
            detail.Text = "入力が開始していません。USB接続とゲーム機の電源を確認してください。";
        } else if (code == 0) {
            status.Text = recordingFile == null ? "停止しました" : "録画を保存しました"; status.ForeColor = ink;
            detail.Text = recordingFile == null ? "プレビュー起動または録画して起動を選んでください。" : "「保存先を開く」から録画したファイルを確認できます。";
        } else {
            string error; lock (outputLock) error = output.ToString(); if (!string.IsNullOrWhiteSpace(backendError)) error = backendError;
            if (error.IndexOf("audio input not found", StringComparison.OrdinalIgnoreCase) >= 0) error = "EasyCAPの音声入力が見つかりません。USB接続を確認してください。";
            else if (error.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0) error = "別のEasyCAPキャプチャーが動作しています。先にそちらを停止してください。";
            else if (error.IndexOf("RenderStream", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("Error opening input", StringComparison.OrdinalIgnoreCase) >= 0) error = "EasyCAPの取り込みを開始できません。ほかのキャプチャーを閉じ、EasyCAPのUSBを差し直して再試行してください。";
            ShowError(string.IsNullOrWhiteSpace(error) ? "起動に失敗しました。EasyCAPの接続とcaptureログを確認してください。" : error.Trim());
        }
        if (closeWhenFinished) Close();
    }
    static void DeleteSessionFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    void ShowError(string message)
    {
        status.Text = "確認が必要です"; status.ForeColor = Color.FromArgb(190, 40, 60);
        detail.Text = message.Length > 500 ? message.Substring(message.Length - 500) : message;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Stop(); timer.Dispose(); }
        base.Dispose(disposing);
    }
}
