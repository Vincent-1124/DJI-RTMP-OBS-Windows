// DJI-RTMP-OBS-Windows — 成品单文件应用（多机位 + 自动诊断）
// 首次运行：安装向导（欢迎 → 下载安装 MediaMTX → 完成指引）。
// 之后运行：直接进主窗口，自动启动服务；可最小化到托盘。
// 单 exe 独立分发：mediamtx.yml 已内嵌为资源，首次运行自动释放到 config/。
// 依赖：.NET Framework 4.8（Windows 10/11 自带），编译见 scripts/build.bat。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DjiRtmpObs
{
    internal static class Program
    {
        private static System.Threading.Mutex _single;

        [STAThread]
        private static void Main()
        {
            bool first;
            _single = new System.Threading.Mutex(true, "DJI-RTMP-OBS-Windows", out first);
            if (!first)
            {
                MessageBox.Show("程序已经在运行了（见右下角托盘图标）。", "DJI RTMP → OBS",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AppForm());
        }
    }

    internal sealed class AppForm : Form
    {
        private const int CamCount = 3;
        private const string MtxVersion = "v1.21.1";
        private const string ZipName = "mediamtx_" + MtxVersion + "_windows_amd64.zip";
        private const string DownloadBase = "https://github.com/bluenviron/mediamtx/releases/download/" + MtxVersion + "/";
        private const string FirewallRule = "DJI-RTMP-OBS MediaMTX";

        private static readonly Color GreenBg = Color.FromArgb(0xE1, 0xF5, 0xEE);
        private static readonly Color GreenFg = Color.FromArgb(0x04, 0x34, 0x2C);
        private static readonly Color YellowBg = Color.FromArgb(0xFA, 0xEE, 0xDA);
        private static readonly Color YellowFg = Color.FromArgb(0x63, 0x38, 0x06);
        private static readonly Color RedBg = Color.FromArgb(0xFC, 0xEB, 0xEB);
        private static readonly Color RedFg = Color.FromArgb(0x79, 0x1F, 0x1F);
        private static readonly Color GrayBg = Color.FromArgb(0xF1, 0xEF, 0xE8);
        private static readonly Color GrayFg = Color.FromArgb(0x44, 0x44, 0x41);
        private static readonly Color Green = Color.FromArgb(0x0F, 0x6E, 0x56);
        private static readonly Color Orange = Color.FromArgb(0xBA, 0x75, 0x17);
        private static readonly Color Red = Color.FromArgb(0xA3, 0x2D, 0x2D);

        private readonly string _root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private readonly Label _lblSvc = new Label();
        private readonly Button _btnToggle = new Button();
        private readonly Panel _pnlDiag = new Panel();
        private readonly Label _lblDiag = new Label();
        private readonly ToolTip _tip = new ToolTip();
        private readonly Label[] _camStat = new Label[CamCount];
        private readonly TextBox[] _camPush = new TextBox[CamCount];
        private readonly bool[] _everPublished = new bool[CamCount];
        private readonly bool[] _prevLive = new bool[CamCount];
        private readonly long[] _prevBytes = new long[CamCount];
        private bool _polled;
        private bool _fwMissing;
        private bool _svcUp;

        private readonly NotifyIcon _tray;
        private readonly Timer _timer;

        private Panel _wizard;
        private Label _wizTitle;
        private Label _wizBody;
        private ProgressBar _wizBar;
        private Label _wizStatus;
        private Button _wizBtn;

        private WebClient _wc;
        private Process _mtx;
        private bool _installing;
        private bool _realExit;

        private string MtxDir { get { return Path.Combine(_root, "mediamtx"); } }
        private string MtxExe { get { return Path.Combine(MtxDir, "mediamtx.exe"); } }
        private string MtxConfDir { get { return Path.Combine(_root, "config"); } }
        private string MtxConf { get { return Path.Combine(MtxConfDir, "mediamtx.yml"); } }
        private string ZipPath { get { return Path.Combine(_root, ZipName); } }
        private string CkPath { get { return Path.Combine(_root, "checksums.sha256"); } }

        public AppForm()
        {
            Text = "DJI RTMP → OBS";
            Icon = MakeIcon();
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(520, 392);
            Font = new Font("Microsoft YaHei UI", 9F);

            EnsureConfig();
            BuildMainUi();

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowMain(); });
            menu.Items.Add("退出", null, delegate { RealExit(); });
            _tray = new NotifyIcon { Icon = Icon, Text = "DJI RTMP → OBS", ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += delegate { ShowMain(); };

            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += OnClosing;
            Load += delegate { OnLoad(); };

            _timer = new Timer { Interval = 3000 };
            _timer.Tick += delegate { RefreshStatus(); };
            _timer.Start();

            if (!File.Exists(MtxExe)) BuildWizard();
        }

        // ---------- 主窗口 ----------

        private void BuildMainUi()
        {
            var hint = new Label();
            hint.Text = "① DJI Mimo 直播里粘贴「推流」地址    ② OBS 媒体源粘贴「OBS」地址（输入格式留空）";
            hint.ForeColor = Color.Gray;
            hint.Font = new Font(Font.FontFamily, 8F);
            hint.AutoSize = true;
            hint.Location = new Point(16, 8);
            Controls.Add(hint);

            for (int i = 0; i < CamCount; i++)
            {
                int cam = i;
                int y = 34 + i * 64;
                int n = i + 1;

                var lblCam = new Label();
                lblCam.Text = "机位" + n + " · camera" + n;
                lblCam.Font = new Font(Font, FontStyle.Bold);
                lblCam.AutoSize = true;
                lblCam.Location = new Point(16, y);
                Controls.Add(lblCam);

                _camStat[i] = new Label();
                _camStat[i].SetBounds(290, y, 204, 17);
                _camStat[i].TextAlign = ContentAlignment.MiddleRight;
                _camStat[i].Text = "— 未使用";
                _camStat[i].ForeColor = Color.Gray;
                Controls.Add(_camStat[i]);

                _camPush[i] = new TextBox();
                _camPush[i].SetBounds(16, y + 22, 282, 23);
                _camPush[i].ReadOnly = true;
                _camPush[i].BackColor = Color.White;
                Controls.Add(_camPush[i]);

                Controls.Add(MkButton("复制推流", 306, y + 21, delegate { Copy(PushUrl(cam)); }, 88, 24));
                Controls.Add(MkButton("复制OBS", 400, y + 21, delegate { Copy(PullUrl(cam)); }, 94, 24));
            }

            _tip.SetToolTip(_camPush[0], "填进 DJI Mimo → 直播 → RTMP");

            _lblSvc.SetBounds(16, 226, 300, 18);
            _lblSvc.Text = "服务状态：—";
            Controls.Add(_lblSvc);

            _pnlDiag.SetBounds(16, 250, 478, 80);
            _pnlDiag.BorderStyle = BorderStyle.FixedSingle;
            _pnlDiag.BackColor = GrayBg;
            _lblDiag.Dock = DockStyle.Fill;
            _lblDiag.Padding = new Padding(8, 6, 8, 6);
            _lblDiag.ForeColor = GrayFg;
            _lblDiag.Text = "等待检测 …";
            _pnlDiag.Controls.Add(_lblDiag);
            _pnlDiag.Click += OnDiagClick;
            _lblDiag.Click += OnDiagClick;
            Controls.Add(_pnlDiag);

            _btnToggle.SetBounds(16, 344, 150, 32);
            _btnToggle.Text = "启动 MediaMTX";
            _btnToggle.Click += delegate { OnToggle(); };
            Controls.Add(_btnToggle);
            Controls.Add(MkButton("刷新地址", 176, 344, delegate { RefreshAddresses(); RefreshStatus(); }, 100, 32));
            Controls.Add(MkButton("打开日志", 286, 344, delegate { OpenLog(); }, 100, 32));
        }

        // ---------- 安装向导 ----------

        private void BuildWizard()
        {
            // 向导期间放大窗口，给正文留足高度，否则长段落会被裁掉
            ClientSize = new Size(560, 480);
            _wizard = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            _wizTitle = new Label { AutoSize = false, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold) };
            _wizTitle.SetBounds(28, 24, 504, 32);
            _wizBody = new Label { AutoSize = false };
            _wizBody.SetBounds(28, 66, 504, 240);
            _wizBar = new ProgressBar();
            _wizBar.SetBounds(28, 320, 504, 14);
            _wizStatus = new Label { AutoSize = false, ForeColor = Color.Gray };
            _wizStatus.SetBounds(28, 340, 504, 42);
            _wizBtn = new Button();
            _wizBtn.SetBounds(28, 412, 170, 32);
            _wizard.Controls.Add(_wizTitle);
            _wizard.Controls.Add(_wizBody);
            _wizard.Controls.Add(_wizBar);
            _wizard.Controls.Add(_wizStatus);
            _wizard.Controls.Add(_wizBtn);
            Controls.Add(_wizard);
            _wizard.BringToFront();
            ShowStep(1);
        }

        private void ShowStep(int step)
        {
            _wizBar.Visible = false;
            _wizStatus.Visible = false;
            _wizBtn.Enabled = true;
            if (step == 1)
            {
                Text = "DJI RTMP → OBS · 安装向导（1/3）";
                _wizTitle.Text = "欢迎使用";
                _wizBody.Text = "本工具把 DJI 运动相机的画面通过局域网无线推流到\r\n这台电脑，交给 OBS 当一个机位（最多同时 3 个机位）。\r\n\r\n使用前请确认：\r\n  · 相机与这台电脑能连到同一个 Wi-Fi（建议 5GHz）\r\n  · 电脑上已安装 OBS\r\n\r\n首次使用需要安装流媒体服务（约 28MB，一次性）。";
                _wizBtn.Text = "开始配置";
                _wizBtn.Click += delegate { ShowStep(2); };
            }
            else if (step == 2)
            {
                Text = "DJI RTMP → OBS · 安装向导（2/3）";
                _wizTitle.Text = "安装流媒体服务";
                _wizBody.Text = "点击下方按钮，自动完成：\r\n  · 下载 MediaMTX（官方发布，SHA-256 校验）\r\n  · 解压到本目录\r\n  · 添加防火墙规则（会弹一次授权窗口，请点\"是\"）\r\n  · 启动服务\r\n\r\n网络较慢属正常，请耐心等待进度条走完。";
                _wizBtn.Text = "下载并安装";
                _wizBtn.Click += delegate { BeginInstall(); };
            }
            else
            {
                Text = "DJI RTMP → OBS · 安装向导（3/3）";
                _wizTitle.Text = "配置完成，服务已启动";
                _wizBody.Text = "以后每次直播：\r\n\r\n1. DJI Mimo 连接相机 → 直播 → RTMP → 粘贴「推流」地址\r\n2. OBS 添加「媒体源」→ 粘贴「OBS」地址\r\n   （输入格式留空，勾选「断开时重新连接」）\r\n3. 主窗口机位状态变成 ● 直播中 即表示成功\r\n\r\n主窗口的诊断面板会在推流异常时自动提示原因与解决办法。";
                _wizBtn.Text = "开始使用";
                _wizBtn.Click += delegate
                {
                    _wizard.Visible = false;
                    _wizard.Dispose();
                    _wizard = null;
                    Text = "DJI RTMP → OBS";
                    ClientSize = new Size(520, 392);   // 向导结束，缩回主窗口尺寸
                    RefreshAddresses();
                };
            }
        }

        // ---------- 安装 ----------

        private void BeginInstall()
        {
            _installing = true;
            _wizBtn.Enabled = false;
            _wizBar.Visible = true;
            _wizStatus.Visible = true;
            _wizStatus.Text = "正在下载 MediaMTX …";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _wc = new WebClient();
            _wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e)
            {
                _wizBar.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
            };
            _wc.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                if (e.Error != null) { InstallFail("下载失败：" + e.Error.Message); return; }
                _wizBar.Value = 100;
                _wizStatus.Text = "正在校验并解压 …";
                var t = new System.Threading.Thread(FinishInstall);
                t.IsBackground = true;
                t.Start();
            };
            _wc.DownloadFileAsync(new Uri(DownloadBase + ZipName), ZipPath);
        }

        private void FinishInstall()
        {
            try
            {
                _wc.DownloadFile(DownloadBase + "checksums.sha256", CkPath);
                VerifyHash();
                Directory.CreateDirectory(MtxDir);
                ZipFile.ExtractToDirectory(ZipPath, MtxDir);
                TryDelete(ZipPath);
                TryDelete(CkPath);
                BeginInvoke(new Action(delegate
                {
                    _wizStatus.Text = "正在添加防火墙规则并启动服务 …";
                    EnsureFirewallRule();
                    StartMtx();
                    _installing = false;
                    ShowStep(3);
                }));
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(delegate { InstallFail("安装失败：" + ex.Message); }));
            }
        }

        private void InstallFail(string msg)
        {
            _installing = false;
            _wizBtn.Enabled = true;
            _wizBtn.Text = "重试";
            _wizBar.Visible = false;
            _wizStatus.Visible = true;
            _wizStatus.Text = msg + "（网络不佳时请先开启代理/加速器）";
        }

        private void VerifyHash()
        {
            string expect = null;
            foreach (var line in File.ReadAllLines(CkPath))
            {
                if (line.EndsWith(ZipName)) { expect = line.Split(' ')[0].Trim().ToLowerInvariant(); break; }
            }
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(ZipPath))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (expect == null || actual != expect)
            {
                TryDelete(ZipPath);
                throw new Exception("SHA-256 校验失败（expected " + (expect ?? "?") + "）");
            }
        }

        // ponytail: 防火墙用端口规则（不踩中文路径编码坑）；加规则必须管理员，触发一次 UAC。
        private static bool FirewallRuleExists()
        {
            try
            {
                var check = Process.Start(new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                string outp = check.StandardOutput.ReadToEnd();
                check.WaitForExit();
                return outp.Contains(FirewallRule);
            }
            catch { return true; }
        }

        private void EnsureFirewallRule()
        {
            try
            {
                Process.Start(new ProcessStartInfo("netsh",
                    "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=TCP localport=1935,9554")
                { Verb = "runas", UseShellExecute = true });
            }
            catch { }
        }

        private void OnDiagClick(object s, EventArgs e)
        {
            if (!_fwMissing) return;
            EnsureFirewallRule();
            _fwMissing = !FirewallRuleExists();
            RefreshStatus();
        }

        // ---------- 服务管理 ----------

        private bool MtxRunning { get { return _mtx != null && !_mtx.HasExited; } }

        private void OnToggle()
        {
            if (MtxRunning) { StopMtx(); return; }
            if (_svcUp)
            {
                // 服务在跑但不是本实例拉起的（孤儿进程）：收编前先清掉
                try { foreach (var p in Process.GetProcessesByName("mediamtx")) { try { p.Kill(); } catch { } } } catch { }
                System.Threading.Thread.Sleep(500);
            }
            StartMtx();
        }

        private void StartMtx()
        {
            if (!File.Exists(MtxExe)) return;
            var psi = new ProcessStartInfo(MtxExe, "\"" + MtxConf + "\"")
            {
                WorkingDirectory = MtxDir,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            try
            {
                _mtx = Process.Start(psi);
                _btnToggle.Text = "停止 MediaMTX";
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopMtx()
        {
            try { if (MtxRunning) _mtx.Kill(); } catch { }
            _mtx = null;
            if (File.Exists(MtxExe)) _btnToggle.Text = "启动 MediaMTX";
        }

        // ---------- 地址 ----------

        // ponytail: UDP connect 不发任何包，只是让系统选出对外网卡，读它的本地 IP。
        private static string LanIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        private static string PushUrl(int cam) { return "rtmp://" + LanIp() + ":1935/live/camera" + (cam + 1); }

        // OBS 与 MediaMTX 同机，接收地址恒为回环地址，不随网络变化。
        private static string PullUrl(int cam) { return "rtsp://127.0.0.1:9554/live/camera" + (cam + 1); }

        private void RefreshAddresses()
        {
            for (int i = 0; i < CamCount; i++)
            {
                _camPush[i].Text = PushUrl(i);
                _tip.SetToolTip(_camPush[i], "填进 DJI Mimo → 直播 → RTMP");
            }
        }

        // ---------- 状态轮询与诊断 ----------

        private void RefreshStatus()
        {
            if (_mtx != null && _mtx.HasExited) _mtx = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:9997/v3/paths/list");
                req.Timeout = 1500;
                req.BeginGetResponse(ar =>
                {
                    string body;
                    try
                    {
                        using (var resp = req.EndGetResponse(ar))
                        using (var sr = new StreamReader(resp.GetResponseStream()))
                            body = sr.ReadToEnd();
                    }
                    catch { body = null; }
                    try { BeginInvoke(new Action(delegate { RenderStatus(body); })); } catch { }
                }, null);
            }
            catch { }
        }

        // ponytail: 不引 JSON 库，用字符串/正则解析 API 返回；MediaMTX 改字段名时再同步。
        private static List<string> PathNames(string body)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(body, "\"name\":\"([^\"]+)\""))
                list.Add(m.Groups[1].Value);
            return list;
        }

        private static string PathSegment(string body, string pathName)
        {
            int idx = body.IndexOf("\"name\":\"" + pathName + "\"");
            if (idx < 0) return null;
            string rest = body.Substring(idx);
            int next = rest.IndexOf("\"name\":", 8);
            return next < 0 ? rest : rest.Substring(0, next);
        }

        private void RenderStatus(string body)
        {
            bool svcUp = body != null;
            _svcUp = svcUp;
            _lblSvc.Text = svcUp ? "服务状态：运行中" : "服务状态：未运行";
            _lblSvc.ForeColor = svcUp ? Green : Red;
            // 按钮三态：自己拉起的→停止；别人拉起的（孤儿）→重启（收编）；没跑→启动
            if (MtxRunning) _btnToggle.Text = "停止 MediaMTX";
            else _btnToggle.Text = _svcUp ? "重启 MediaMTX" : "启动 MediaMTX";

            var names = svcUp ? PathNames(body) : new List<string>();
            int firstObsMissing = -1, firstInterrupted = -1;
            bool anyLive = false;
            var liveList = new List<string>();
            string liveRate = "";

            for (int i = 0; i < CamCount; i++)
            {
                string pathName = "live/camera" + (i + 1);
                string seg = svcUp ? PathSegment(body, pathName) : null;
                bool publishing = seg != null && seg.Contains("\"ready\":true");
                bool hasReaders = publishing && seg.Contains("\"readers\":[{");

                long bytes = 0;
                if (publishing)
                {
                    var mb = Regex.Match(seg, "\"bytesReceived\":(\\d+)");
                    if (mb.Success) bytes = long.Parse(mb.Groups[1].Value);
                }
                double mbps = (publishing && _prevBytes[i] > 0 && bytes > _prevBytes[i])
                    ? (bytes - _prevBytes[i]) * 8.0 / 3.0 / 1000000.0 : 0;
                _prevBytes[i] = bytes;

                string st;
                Color sc;
                if (publishing && hasReaders) { st = "● 直播中"; sc = Green; }
                else if (publishing) { st = "● 已推流 · OBS 未拉"; sc = Orange; if (firstObsMissing < 0) firstObsMissing = i; }
                else if (_everPublished[i]) { st = "○ 已断流"; sc = Orange; if (firstInterrupted < 0) firstInterrupted = i; }
                else { st = "— 未使用"; sc = Color.Gray; }

                if (publishing && mbps > 0) st += " · " + mbps.ToString("0.0") + " Mbps";
                _camStat[i].Text = st;
                _camStat[i].ForeColor = sc;

                if (publishing) _everPublished[i] = true;
                if (publishing && hasReaders)
                {
                    anyLive = true;
                    liveList.Add("机位" + (i + 1));
                    if (mbps > 0) liveRate = " · " + mbps.ToString("0.0") + " Mbps";
                }

                bool liveNow = publishing && hasReaders;
                if (_polled && liveNow != _prevLive[i])
                    _tray.ShowBalloonTip(2000, "DJI RTMP → OBS", "机位" + (i + 1) + (liveNow ? " 开始直播" : " 推流中断"), ToolTipIcon.Info);
                _prevLive[i] = liveNow;
            }
            _polled = true;

            string unexpected = null;
            foreach (var n in names)
            {
                if (n != "live/camera1" && n != "live/camera2" && n != "live/camera3") { unexpected = n; break; }
            }

            string diag;
            Color bg, fg;
            if (!svcUp)
            {
                diag = "服务未运行：点下方「启动 MediaMTX」。";
                bg = GrayBg; fg = GrayFg;
            }
            else if (unexpected != null)
            {
                diag = "路径不匹配：相机正在推流到 '" + unexpected + "'，与机位路径都不一致——Mimo 里的推流地址末尾可能多了字符（比如句点）。改正 Mimo 地址；或临时把 OBS 地址改成 '" + unexpected + "' 先验证。";
                bg = RedBg; fg = RedFg;
            }
            else if (firstObsMissing >= 0)
            {
                int n = firstObsMissing + 1;
                diag = "机位" + n + " 相机已在推流，但 OBS 未拉流：检查 OBS 媒体源地址 rtsp://127.0.0.1:9554/live/camera" + n + "（「输入格式」留空、勾选「断开时重新连接」）。";
                bg = YellowBg; fg = YellowFg;
            }
            else if (firstInterrupted >= 0)
            {
                diag = "机位" + (firstInterrupted + 1) + " 推流中断：等待相机重连，OBS 会自动恢复。";
                bg = YellowBg; fg = YellowFg;
            }
            else if (_fwMissing)
            {
                diag = "防火墙规则缺失，相机可能连不上这台电脑。点击此面板一键修复（需授权一次）。";
                bg = YellowBg; fg = YellowFg;
            }
            else if (anyLive)
            {
                diag = "✓ " + string.Join("、", liveList.ToArray()) + " 直播中，画面正在进入 OBS。" + liveRate;
                bg = GreenBg; fg = GreenFg;
            }
            else
            {
                diag = "等待推流：在 DJI Mimo 中点「开始直播」。若 Mimo 报「检查网络」，确认相机与本机在同一 Wi-Fi。";
                bg = GrayBg; fg = GrayFg;
            }

            _pnlDiag.BackColor = bg;
            _lblDiag.Text = diag;
            _lblDiag.ForeColor = fg;
            _pnlDiag.Cursor = (_fwMissing && svcUp) ? Cursors.Hand : Cursors.Default;
        }

        private void Copy(string text)
        {
            try
            {
                Clipboard.SetText(text);
                _tray.ShowBalloonTip(1200, "已复制", text, ToolTipIcon.Info);
            }
            catch { }
        }

        private void OpenLog()
        {
            string log = Path.Combine(MtxDir, "mediamtx.log");
            if (File.Exists(log)) Process.Start("notepad.exe", log);
            else MessageBox.Show("还没有日志文件（服务启动后生成）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- 窗口与托盘 ----------

        private void OnLoad()
        {
            RefreshAddresses();
            if (File.Exists(MtxExe))
            {
                StartMtx();
                // 自检：规则缺失时才会弹 UAC，存在则静默跳过（覆盖"未走向导"的老用户）
                if (!FirewallRuleExists())
                {
                    EnsureFirewallRule();
                    _fwMissing = !FirewallRuleExists();
                }
            }
        }

        private void OnClosing(object s, FormClosingEventArgs e)
        {
            if (!_realExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            _timer.Stop();
            StopMtx();
            _tray.Visible = false;
            _tray.Dispose();
        }

        private void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void RealExit()
        {
            _realExit = true;
            Close();
        }

        // ---------- 小工具 ----------

        // 单 exe 分发：mediamtx.yml 内嵌为资源，首次运行释放到 config/。
        private void EnsureConfig()
        {
            if (File.Exists(MtxConf)) return;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mediamtx.yml"))
                {
                    if (s == null) return;
                    Directory.CreateDirectory(MtxConfDir);
                    using (var f = File.Create(MtxConf)) s.CopyTo(f);
                }
            }
            catch { }
        }

        private static Button MkButton(string text, int x, int y, EventHandler fn, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.Click += fn;
            return b;
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 95, 165));
                using (var f = new Font("Segoe UI", 15, FontStyle.Bold))
                using (var br = new SolidBrush(Color.White))
                {
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("R", f, br, new RectangleF(0, -2, 32, 34), fmt);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
