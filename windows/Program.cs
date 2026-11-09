// Mi50FanWin — Windows 10/11 port of the Linux mi50fan controller.
// Controls the motherboard 4-pin header that the MI50/Pro-VII cooler fan is
// plugged into (typical Chinese mod-card setup), reads GPU temperature via
// LibreHardwareMonitor, applies curve with hard cap 75% below 70 C.
// Run as Administrator (needs Ring0 driver for SuperIO). MIT.
using LibreHardwareMonitor.Hardware;
using System.Reflection;
using System.Security.Principal;

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("ERROR: run as Administrator (SuperIO Ring0 driver needed).");
    return 1;
}

bool detectOnly = args.Contains("--detect");

// The concrete Control class is internal in LibreHardwareMonitorLib; its
// ControlMode/SoftwareValue setters are reachable through reflection.
static void SetMode(object ctl, ControlMode m) =>
    ctl.GetType().GetProperty("ControlMode")!.GetSetMethod(true)!.Invoke(ctl, new object[] { m });
static void SetVal(object ctl, float v) =>
    ctl.GetType().GetProperty("SoftwareValue")!.GetSetMethod(true)!.Invoke(ctl, new object[] { v });
static float GetVal(object ctl) =>
    (float)ctl.GetType().GetProperty("SoftwareValue")!.GetGetMethod(true)!.Invoke(ctl, null)!;

var computer = new Computer
{
    IsGpuEnabled = true,
    IsMotherboardEnabled = true,
    IsCpuEnabled = false,
    IsMemoryEnabled = false,
    IsPsuEnabled = false,
    IsStorageEnabled = false,
    IsControllerEnabled = false,
    IsNetworkEnabled = false,
};
computer.Open();

static float GpuTemp(Computer pc)
{
    float worst = 0f;
    foreach (var hw in pc.Hardware)
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

if (args.Contains("--temp"))
{
    for (int i = 0; i < 5; i++) { Console.WriteLine($"gpu={GpuTemp(computer):0.#}C"); Thread.Sleep(2000); }
    computer.Close();
    return 0;
}

var pairs = new List<(string Name, ISensor Fan, IControl Control)>();
foreach (var hw in computer.Hardware)
{
    if (hw.HardwareType != HardwareType.Motherboard) continue;
    hw.Update();
    Console.WriteLine($"Motherboard: {hw.Name}");
    var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
    var ctls = hw.Sensors.OfType<IControl>().ToList();
    Console.WriteLine($"  fan sensors: {fans.Count}, controllable: {ctls.Count}");
    foreach (var ctl in ctls)
    {
        var prop = ctl.GetType().GetProperty("Sensor");
        ISensor? fan = prop?.GetValue(ctl) as ISensor;
        if (fan == null || fan.SensorType != SensorType.Fan)
            fan = fans.FirstOrDefault(f => f.Identifier.ToString() == ctl.Identifier.ToString().Replace("control", "fan"));
        if (fan == null) continue;
        pairs.Add(($"{hw.Name} / {fan.Name}", fan, ctl));
    }
}
if (pairs.Count == 0) { Console.WriteLine("No SuperIO fan+control pair found. Check BIOS SuperIO monitoring / chip support."); return 2; }

var wired = new List<((string Name, ISensor Fan, IControl Control) P, double Hi)>();
foreach (var p in pairs)
{
    double base0 = p.Fan.Value ?? 0;
    try
    {
        SetMode(p.Control, ControlMode.Software);
        SetVal(p.Control, 20f); Thread.Sleep(3000);
        double lo = p.Fan.Value ?? 0;
        SetVal(p.Control, 100f); Thread.Sleep(3000);
        double hi = p.Fan.Value ?? 0;
        Console.WriteLine($"  {p.Name}: base={base0:0} low={lo:0} high={hi:0} RPM");
        if (hi - lo > 100) wired.Add((p, hi));
    }
    catch (Exception e) { Console.WriteLine($"  {p.Name}: control failed: {e.Message}"); }
}
if (detectOnly) { computer.Close(); return 0; }
if (wired.Count == 0)
{
    Console.WriteLine("No header responded to PWM sweep. Re-check 4-pin wiring; falling back to all headers.");
    foreach (var p in pairs) wired.Add((p, p.Fan.Value ?? 0));
}
wired.Sort((a, b) => b.Hi.CompareTo(a.Hi));
var chosen = wired[0].P;
Console.WriteLine($"MANAGING: {chosen.Name} (tach {wired[0].Hi:0} RPM)");

static double CurvePct(double t)
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

const double StepLimit = 12.0;
const double HystC = 1.5;
var hist = new Queue<double>();
double curPct = GetVal(chosen.Control);

while (true)
{
    float t = GpuTemp(computer);
    if (t <= 0f) { Thread.Sleep(2000); continue; }
    hist.Enqueue(t);
    while (hist.Count > 30) hist.Dequeue();
    double tEff = hist.Max();
    double tgt = CurvePct(tEff);
    if (tEff < 70 && tgt > 75) tgt = 75;

    if (tgt > curPct) curPct = Math.Min(tgt, curPct + StepLimit);
    else if (tgt < curPct && (t < tEff - HystC || tEff - t < 0.5)) curPct = Math.Max(tgt, curPct - StepLimit);

    try { SetVal(chosen.Control, (float)curPct); } catch { }
    Console.WriteLine($"gpu={t:0.#}C eff={tEff:0.#}C -> {curPct:0}% fan={chosen.Fan.Value ?? 0:0}RPM");
    Thread.Sleep(2000);
}
