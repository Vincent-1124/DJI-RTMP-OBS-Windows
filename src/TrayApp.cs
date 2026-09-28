// DJI-RTMP-OBS-Windows — 托盘工具
// 职责：显示/复制推流地址与 OBS 接收地址、启停 MediaMTX、轮询流状态。
// 依赖：.NET Framework 4.8（Windows 10/11 自带），编译见 scripts/build.bat。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
            Application.Run(new TrayApp());
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const string StreamPath = "live/camera1";
        private readonly string _root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _miPush;
        private readonly ToolStripMenuItem _miPull;
        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miToggle;
        private readonly Timer _timer;
        private Process _mtx;

        private string MtxExe { get { return Path.Combine(_root, "mediamtx", "mediamtx.exe"); } }
        private string MtxConf { get { return Path.Combine(_root, "config", "mediamtx.yml"); } }

        public TrayApp()
        {
            var menu = new ContextMenuStrip();
            _miPush = new ToolStripMenuItem("", null, delegate { Copy(PushUrl(), "推流地址"); });
            _miPull = new ToolStripMenuItem("", null, delegate { Copy(PullUrl(), "OBS 接收地址"); });
            _miStatus = new ToolStripMenuItem("流状态：检测中…") { Enabled = false };
            _miToggle = new ToolStripMenuItem("启动 MediaMTX", null, delegate { ToggleMtx(); });

            menu.Items.Add(_miPush);
            menu.Items.Add(_miPull);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miStatus);
            menu.Items.Add(_miToggle);
            menu.Items.Add(new ToolStripMenuItem("刷新地址", null, delegate { RefreshAddresses(); }));
            menu.Items.Add(new ToolStripMenuItem("打开目录", null, delegate { Process.Start(_root); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Exit(); }));

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "DJI RTMP → OBS",
                ContextMenuStrip = menu,
                Visible = true
            };

            Application.ApplicationExit += delegate { StopMtx(); };
            RefreshAddresses();

            _timer = new Timer { Interval = 3000 };
            _timer.Tick += delegate { RefreshStatus(); };
            _timer.Start();
        }

        // ponytail: UDP connect 不发任何包，只是让系统选出对外网卡，读它的本地 IP；
        // 多网卡选错时用托盘「刷新地址」重取，换 NIC 枚举属过度设计。
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
            _miPush.Text = "推流地址（复制）：" + PushUrl();
            _miPull.Text = "OBS 接收（复制）：" + PullUrl();
        }

        private void Copy(string text, string what)
        {
            try
            {
                Clipboard.SetText(text);
                _tray.ShowBalloonTip(1500, "已复制" + what, text, ToolTipIcon.Info);
            }
            catch { }
        }

        private bool MtxRunning { get { return _mtx != null && !_mtx.HasExited; } }

        private void ToggleMtx()
        {
            if (MtxRunning) StopMtx(); else StartMtx();
        }

        private void StartMtx()
        {
            if (!File.Exists(MtxExe))
            {
                MessageBox.Show("未找到 mediamtx.exe\r\n请先以管理员身份运行 scripts\\install.ps1", "DJI RTMP → OBS",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var psi = new ProcessStartInfo(MtxExe, "\"" + MtxConf + "\"")
            {
                WorkingDirectory = Path.GetDirectoryName(MtxExe),
                CreateNoWindow = true,
                UseShellExecute = false
            };
            try
            {
                _mtx = Process.Start(psi);
                _miToggle.Text = "停止 MediaMTX";
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "DJI RTMP → OBS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopMtx()
        {
            try { if (MtxRunning) _mtx.Kill(); } catch { }
            _mtx = null;
            if (_miToggle != null) _miToggle.Text = "启动 MediaMTX";
        }

        // ponytail: 不引 JSON 库，响应里搜路径名和 ready 标记即可判断是否在播；
        // 仅当 MediaMTX 改版 API 字段名时需要同步改这里。
        private void RefreshStatus()
        {
            if (_mtx != null && _mtx.HasExited) { _mtx = null; _miToggle.Text = "启动 MediaMTX"; }
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
                    catch { SetStatus("流状态：服务未运行"); return; }
                    SetStatus(body.Contains("camera1") && body.Contains("\"ready\":true")
                        ? "流状态：● 直播中"
                        : "流状态：等待推流");
                }, null);
            }
            catch { }
        }

        private void SetStatus(string text)
        {
            var menu = _tray.ContextMenuStrip;
            if (menu.InvokeRequired) menu.BeginInvoke(new Action(delegate { _miStatus.Text = text; }));
            else _miStatus.Text = text;
        }

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

        private void Exit()
        {
            _timer.Stop();
            StopMtx();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }
    }
}
