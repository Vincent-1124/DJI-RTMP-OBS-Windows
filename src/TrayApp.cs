// DJI-RTMP-OBS-Windows — 成品单文件应用
// 首次运行：安装向导（欢迎 → 下载安装 MediaMTX → 完成指引）。
// 之后运行：直接进主窗口，自动启动服务；可最小化到托盘。
// 单 exe 独立分发：mediamtx.yml 已内嵌为资源，首次运行自动释放到 config/。
// 依赖：.NET Framework 4.8（Windows 10/11 自带），编译见 scripts/build.bat。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace DjiRtmpObs
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AppForm());
        }
    }

    internal sealed class AppForm : Form
    {
        private const string StreamPath = "live/camera1";
        private const string MtxVersion = "v1.21.1";
        private const string ZipName = "mediamtx_" + MtxVersion + "_windows_amd64.zip";
        private const string DownloadBase = "https://github.com/bluenviron/mediamtx/releases/download/" + MtxVersion + "/";
        private const string FirewallRule = "DJI-RTMP-OBS MediaMTX";

        private readonly string _root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private readonly TextBox _txtPush = new TextBox();
        private readonly TextBox _txtPull = new TextBox();
        private readonly Label _lblSvc = new Label();
        private readonly Label _lblStream = new Label();
        private readonly Button _btnToggle = new Button();
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
            ClientSize = new Size(520, 300);
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
            Controls.Add(MkLabel("推流地址（填进 DJI Mimo）：", 16, 16));
            SetupBox(_txtPush, 16, 40);
            Controls.Add(_txtPush);
            Controls.Add(MkButton("复制", 394, 39, delegate { Copy(PushUrl()); }));

            Controls.Add(MkLabel("OBS 接收地址（媒体源 → 输入）：", 16, 78));
            SetupBox(_txtPull, 16, 102);
            Controls.Add(_txtPull);
            Controls.Add(MkButton("复制", 394, 101, delegate { Copy(PullUrl()); }));

            _lblSvc.SetBounds(16, 144, 200, 20);
            _lblSvc.Text = "服务状态：—";
            _lblStream.SetBounds(224, 144, 270, 20);
            _lblStream.Text = "流状态：—";
            Controls.Add(_lblSvc);
            Controls.Add(_lblStream);

            _btnToggle.SetBounds(16, 176, 200, 34);
            _btnToggle.Text = "启动 MediaMTX";
            _btnToggle.Click += delegate { OnToggle(); };
            Controls.Add(_btnToggle);
            Controls.Add(MkButton("刷新地址", 228, 176, delegate { RefreshAddresses(); }, 120, 34));
        }

        // ---------- 安装向导 ----------

        private void BuildWizard()
        {
            _wizard = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            _wizTitle = new Label { AutoSize = false, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold) };
            _wizTitle.SetBounds(24, 24, 460, 28);
            _wizBody = new Label { AutoSize = false };
            _wizBody.SetBounds(24, 64, 460, 130);
            _wizBar = new ProgressBar();
            _wizBar.SetBounds(24, 200, 460, 14);
            _wizStatus = new Label { AutoSize = false, ForeColor = Color.Gray };
            _wizStatus.SetBounds(24, 222, 460, 36);
            _wizBtn = new Button();
            _wizBtn.SetBounds(24, 262, 160, 30);
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
                _wizBody.Text = "本工具把 DJI 运动相机的画面通过局域网无线推流到\r\n这台电脑，交给 OBS 当一个机位。\r\n\r\n使用前请确认：\r\n  · 相机与这台电脑能连到同一个 Wi-Fi（建议 5GHz）\r\n  · 电脑上已安装 OBS\r\n\r\n首次使用需要安装流媒体服务（约 28MB，一次性）。";
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
                _wizBody.Text = "以后每次直播：\r\n\r\n1. DJI Mimo 连接相机 → 直播 → RTMP → 粘贴推流地址\r\n2. OBS 添加「媒体源」→ 粘贴 OBS 接收地址\r\n   （输入格式留空，勾选「断开时重新连接」）\r\n3. 主窗口「流状态」变成 ● 直播中 即表示成功\r\n\r\n两个地址在主窗口可一键复制，换网络后点「刷新地址」。";
                _wizBtn.Text = "开始使用";
                _wizBtn.Click += delegate
                {
                    _wizard.Visible = false;
                    _wizard.Dispose();
                    _wizard = null;
                    Text = "DJI RTMP → OBS";
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

        // ponytail: 加防火墙规则必须管理员，这里触发一次 UAC；用户取消则首次绑定端口时由 Windows 弹窗兜底。
        private void EnsureFirewallRule()
        {
            try
            {
                var check = Process.Start(new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                string outp = check.StandardOutput.ReadToEnd();
                check.WaitForExit();
                if (outp.Contains(FirewallRule)) return;
            }
            catch { }
            try
            {
                // ponytail: 用端口规则而不是 program= 路径规则——不踩中文路径编码坑，exe 挪位置也不失效。
                Process.Start(new ProcessStartInfo("netsh",
                    "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=TCP localport=1935,9554")
                { Verb = "runas", UseShellExecute = true });
            }
            catch { }
        }

        // ---------- 服务管理 ----------

        private bool MtxRunning { get { return _mtx != null && !_mtx.HasExited; } }

        private void OnToggle()
        {
            if (MtxRunning) StopMtx(); else StartMtx();
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

        // ---------- 状态与地址 ----------

        // ponytail: UDP connect 不发任何包，只是让系统选出对外网卡，读它的本地 IP；
        // 多网卡选错时点「刷新地址」重取，枚举所有 NIC 属过度设计。
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

        private static string PushUrl() { return "rtmp://" + LanIp() + ":1935/" + StreamPath; }

        // OBS 与 MediaMTX 同机，接收地址恒为回环地址，不随网络变化，无需刷新。
        private static string PullUrl() { return "rtsp://127.0.0.1:9554/" + StreamPath; }

        private void RefreshAddresses()
        {
            _txtPush.Text = PushUrl();
            _txtPull.Text = PullUrl();
        }

        // ponytail: 不引 JSON 库，响应里搜路径名和 ready 标记即可判断是否在播；
        // 仅当 MediaMTX 改版 API 字段名时需要同步改这里。
        private void RefreshStatus()
        {
            if (_mtx != null && _mtx.HasExited)
            {
                _mtx = null;
                if (File.Exists(MtxExe)) _btnToggle.Text = "启动 MediaMTX";
            }
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
                    catch { SetStatus(false, false); return; }
                    SetStatus(true, body.Contains("camera1") && body.Contains("\"ready\":true"));
                }, null);
            }
            catch { }
        }

        private void SetStatus(bool svcUp, bool live)
        {
            if (InvokeRequired) { BeginInvoke(new Action(delegate { SetStatus(svcUp, live); })); return; }
            _lblSvc.Text = svcUp ? "服务状态：运行中" : "服务状态：未运行";
            _lblSvc.ForeColor = svcUp ? Color.FromArgb(15, 110, 86) : Color.FromArgb(163, 45, 45);
            _lblStream.Text = live ? "流状态：● 直播中" : (svcUp ? "流状态：等待推流" : "流状态：—");
            _lblStream.ForeColor = live ? Color.FromArgb(15, 110, 86) : Color.Gray;
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

        // ---------- 窗口与托盘 ----------

        private void OnLoad()
        {
            RefreshAddresses();
            if (File.Exists(MtxExe))
            {
                StartMtx();
                // 自检：规则缺失时才会弹 UAC，存在则静默跳过（覆盖"未走向导"的老用户）
                EnsureFirewallRule();
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

        private static Label MkLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        private static Button MkButton(string text, int x, int y, EventHandler fn) { return MkButton(text, x, y, fn, 100, 25); }

        private static Button MkButton(string text, int x, int y, EventHandler fn, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.Click += fn;
            return b;
        }

        private static void SetupBox(TextBox t, int x, int y)
        {
            t.SetBounds(x, y, 370, 23);
            t.ReadOnly = true;
            t.BackColor = Color.White;
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
