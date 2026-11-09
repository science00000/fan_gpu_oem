// Mi50FanWin — Windows 10/11 fan controller for MI50/Pro-VII mod cards whose
// cooler fan is wired to a motherboard 4-pin header. Auto-detects the header
// (PWM sweep -> tach response), curve control, hard cap 75% below 70 C.
// Modes: Mi50FanWin.exe            -> console controller
//        Mi50FanWin.exe --gui      -> small always-visible monitor window
//        Mi50FanWin.exe --detect   -> list headers + sweep, then exit
//        Mi50FanWin.exe --temp     -> print GPU temp 5x, then exit
// Run as Administrator (Ring0 driver for SuperIO). MIT.
using LibreHardwareMonitor.Hardware;
using System.Reflection;
using System.Security.Principal;

internal static class Program
{
    static int Main(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("ERROR: run as Administrator (SuperIO Ring0 driver needed).");
            return 1;
        }
        var hub = new Hub();
        if (args.Contains("--temp"))
        {
            for (int i = 0; i < 5; i++) { Console.WriteLine($"gpu={hub.GpuTemp():0.#}C"); Thread.Sleep(2000); }
            return 0;
        }
        if (args.Contains("--detect"))
        {
            hub.Detect(sweep: true);
            return 0;
        }
        bool gui = args.Contains("--gui");
        if (gui)
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new MonitorForm(hub);
            Application.Run(form);
            return 0;
        }
        // console mode
        foreach (var l in hub.Detect(sweep: true)) Console.WriteLine(l);
        hub.StartControl();
        while (true)
        {
            var s = hub.Tick();
            Console.WriteLine($"gpu={s.Temp:0.#}C eff={s.Eff:0.#}C -> {s.Pct:0}% fan={s.Rpm:0}RPM");
            Thread.Sleep(2000);
        }
    }
}

internal sealed class Hub
{
    public sealed record Status(float Temp, double Eff, double Pct, double Rpm, string Header, string State);

    private readonly Computer _pc;
    private readonly List<(string Name, ISensor Fan, IControl Control)> _pairs = new();
    private (string Name, ISensor Fan, IControl Control)? _chosen;
    private readonly Queue<double> _hist = new();
    private double _curPct = 50;
    private string _state = "init";

    public Hub()
    {
        _pc = new Computer
        {
            IsGpuEnabled = true, IsMotherboardEnabled = true, IsCpuEnabled = false,
            IsMemoryEnabled = false, IsPsuEnabled = false, IsStorageEnabled = false,
            IsControllerEnabled = false, IsNetworkEnabled = false,
        };
        _pc.Open();
    }

    // Internal Control class in LibreHardwareMonitorLib: reach setters via reflection.
    private static void SetMode(object ctl, ControlMode m) =>
        ctl.GetType().GetProperty("ControlMode")!.GetSetMethod(true)!.Invoke(ctl, new object[] { m });
    private static void SetVal(object ctl, float v) =>
        ctl.GetType().GetProperty("SoftwareValue")!.GetSetMethod(true)!.Invoke(ctl, new object[] { v });
    private static float GetVal(object ctl) =>
        (float)ctl.GetType().GetProperty("SoftwareValue")!.GetGetMethod(true)!.Invoke(ctl, null)!;

    public float GpuTemp()
    {
        float worst = 0f;
        foreach (var hw in _pc.Hardware)
        {
            if (hw.HardwareType != HardwareType.GpuAmd && hw.HardwareType != HardwareType.GpuNvidia) continue;
            hw.Update();
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value == null) continue;
                string n = (s.Name ?? "").ToLowerInvariant();
                float v = s.Value.Value, w = v;
                if (n.Contains("hot spot")) w = v - 2;
                else if (n.Contains("memory")) w = v + 3;
                worst = Math.Max(worst, w);
            }
        }
        return worst;
    }

    public List<string> Detect(bool sweep)
    {
        var log = new List<string>();
        foreach (var hw in _pc.Hardware)
        {
            if (hw.HardwareType != HardwareType.Motherboard) continue;
            hw.Update();
            log.Add($"Motherboard: {hw.Name}");
            var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            foreach (var ctl in hw.Sensors.OfType<IControl>())
            {
                ISensor fan = ctl.GetType().GetProperty("Sensor")?.GetValue(ctl) as ISensor;
                if (fan == null || fan.SensorType != SensorType.Fan)
                    fan = fans.FirstOrDefault(f => f.Identifier.ToString() == ctl.Identifier.ToString().Replace("control", "fan"));
                if (fan == null) continue;
                _pairs.Add(($"{hw.Name} / {fan.Name}", fan, ctl));
            }
        }
        log.Add($"fan headers: {_pairs.Count}");
        if (_pairs.Count == 0) { _state = "no SuperIO headers"; return log; }

        if (!sweep) { Choose(_pairs.Select(p => (p, (double)(p.Fan.Value ?? 0f))).ToList()); return log; }

        var wired = new List<((string Name, ISensor Fan, IControl Control) P, double Hi)>();
        foreach (var p in _pairs)
        {
            try
            {
                double base0 = p.Fan.Value ?? 0;
                SetMode(p.Control, ControlMode.Software);
                SetVal(p.Control, 20f); Thread.Sleep(3000);
                double lo = p.Fan.Value ?? 0;
                SetVal(p.Control, 100f); Thread.Sleep(3000);
                double hi = p.Fan.Value ?? 0;
                log.Add($"  {p.Name}: base={base0:0} low={lo:0} high={hi:0} RPM");
                if (hi - lo > 100) wired.Add((p, hi));
            }
            catch (Exception e) { log.Add($"  {p.Name}: control failed: {e.Message}"); }
        }
        if (wired.Count == 0)
        {
            log.Add("no header responded to sweep; managing all headers blindly");
            wired = _pairs.Select(p => (p, p.Fan.Value ?? 0.0)).ToList();
        }
        Choose(wired);
        _state = "controlling";
        return log;
    }

    private void Choose(List<((string Name, ISensor Fan, IControl Control) P, double Hi)> wired)
    {
        wired.Sort((a, b) => b.Hi.CompareTo(a.Hi));
        _chosen = wired[0].P;
        try { SetMode(_chosen.Value.Control, ControlMode.Software); } catch { }
        _curPct = GetVal(_chosen.Value.Control);
    }

    public void StartControl() => _state = "controlling";

    public static double CurvePct(double t)
    {
        (double T, double P)[] curve = { (0,15),(40,20),(50,35),(58,50),(65,65),(70,75),(75,88),(82,100),(90,100) };
        if (t <= curve[0].T) return curve[0].P;
        for (int i = 0; i + 1 < curve.Length; i++)
        {
            var (t0, p0) = curve[i]; var (t1, p1) = curve[i + 1];
            if (t <= t1) return p0 + (p1 - p0) * (t - t0) / (t1 - t0);
        }
        return curve[^1].P;
    }

    public Status Tick()
    {
        float t = GpuTemp();
        if (_chosen == null || _state != "controlling" || t <= 0f)
            return new Status(t, t, _curPct, _chosen?.Fan.Value ?? 0, _chosen?.Name ?? "-", _state);

        _hist.Enqueue(t);
        while (_hist.Count > 30) _hist.Dequeue();
        double tEff = _hist.Max();
        double tgt = CurvePct(tEff);
        if (tEff < 70 && tgt > 75) tgt = 75;          // hard cap below 70C

        if (tgt > _curPct) _curPct = Math.Min(tgt, _curPct + 12);
        else if (tgt < _curPct && (t < tEff - 1.5 || tEff - t < 0.5)) _curPct = Math.Max(tgt, _curPct - 12);

        try { SetVal(_chosen.Value.Control, (float)_curPct); } catch { }
        return new Status(t, tEff, _curPct, _chosen.Value.Fan.Value ?? 0, _chosen.Value.Name, _state);
    }
}

internal sealed class MonitorForm : Form
{
    private readonly Hub _hub;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly NotifyIcon _tray = new();
    private bool _exiting;
    private readonly Label _big = new();
    private readonly Label _sub = new();
    private readonly Label _status = new();
    private readonly CurvePanel _curve = new();
    private Hub.Status? _last;

    public MonitorForm(Hub hub)
    {
        _hub = hub;
        Text = "Mi50Fan";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 380, 20);
        ClientSize = new Size(360, 190);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.Gainsboro;
        ShowInTaskbar = true;
        MinimizeBox = true;

        _big.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        _big.Location = new Point(14, 8); _big.AutoSize = true;
        _sub.Font = new Font("Consolas", 10f);
        _sub.Location = new Point(16, 56); _sub.AutoSize = true;
        _status.Font = new Font("Segoe UI", 8.5f);
        _status.Location = new Point(16, 168); _status.AutoSize = true;
        _status.ForeColor = Color.DimGray;
        _curve.Location = new Point(200, 8); _curve.Size = new Size(150, 150);

        Controls.Add(_big); Controls.Add(_sub); Controls.Add(_status); Controls.Add(_curve);

        // minimize button hides to tray; double-click tray icon restores
        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mo", null, (_, _) => ShowFromTray());
        menu.Items.Add("Thoat", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };

        _status.Text = "detecting fan headers...";
        _big.Text = "-- C";
        Load += (_, _) =>
        {
            Task.Run(() => _hub.Detect(sweep: true));   // background; UI keeps updating
            _timer.Tick += (_, _) => RefreshUi();
            _timer.Start();
            RefreshUi();
        };
        FormClosed += (_, _) => { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); };
    }

    private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void RefreshUi()
    {
        _last = _hub.Tick();
        var s = _last;
        _big.Text = $"{s.Temp:0.#} C";
        _big.ForeColor = s.Temp >= 70 ? Color.OrangeRed : s.Temp >= 58 ? Color.Khaki : Color.MediumAquamarine;
        _sub.Text = $"fan {s.Pct:0}%   {s.Rpm:0} RPM";
        _status.Text = $"{s.State} | {s.Header}";
        _tray.Text = $"Mi50Fan {s.Temp:0.#}C fan {s.Pct:0}% {s.Rpm:0}RPM";
        _curve.Current = s;
        _curve.Invalidate();
    }

    private sealed class CurvePanel : Panel
    {
        public Hub.Status? Current;
        public CurvePanel() { DoubleBuffered = true; BackColor = Color.FromArgb(24, 26, 30); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            const float tMin = 30, tMax = 90;
            int W = Width, H = Height;
            Func<double, double, Point> pt = (t, p) =>
                new Point((int)((t - tMin) / (tMax - tMin) * (W - 4)) + 2, H - 2 - (int)(p / 100.0 * (H - 4)));
            using var pen = new Pen(Color.SteelBlue, 2);
            var prev = pt(tMin, Hub.CurvePct(tMin));
            for (double t = tMin + 2; t <= tMax; t += 2)
            { var nxt = pt(t, Hub.CurvePct(t)); g.DrawLine(pen, prev, nxt); prev = nxt; }
            using var cap = new Pen(Color.FromArgb(90, Color.OrangeRed)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(cap, pt(70, 0), pt(70, 100));
            if (Current != null && Current.Temp > 0)
            {
                using var brush = new SolidBrush(Color.Orange);
                var p = pt(Math.Clamp(Current.Temp, tMin, tMax), Current.Pct);
                g.FillEllipse(brush, p.X - 4, p.Y - 4, 8, 8);
            }
        }
    }
}
