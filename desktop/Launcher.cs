using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AnsCheckin
{
    /// <summary>
    /// ANS 每日签到 · 桌面启动器
    ///
    /// 职责：启动本地控制台服务（隐藏窗口）→ 让服务端打开界面 → 常驻托盘 →
    ///       监听执行记录并弹桌面通知 → 退出时关闭后台服务。
    ///
    /// 命令行参数：
    ///   （无）              正常启动：起服务 + 打开控制台窗口
    ///   auto               开机自启用：只起服务和托盘，不弹窗口
    ///   autostart-on       创建开机自启快捷方式后退出
    ///   autostart-off      删除开机自启快捷方式后退出
    /// </summary>
    internal static class Program
    {
        private static NotifyIcon _tray;
        private static Process _node;
        private static string _root;
        private static int _port = 8787;
        private static long _runsOffset = -1;
        private static System.Windows.Forms.Timer _watch;
        private static volatile bool _exiting;
        private static bool _autoMode;
        private static string _logFile;

        private const string TaskTrayTitle = "ANS 每日签到";
        private const string AutoStartLnkName = "ANS每日签到.lnk";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // ---------------------------------------------------------------- 日志

        private static void Log(string message)
        {
            try
            {
                if (_logFile == null)
                {
                    string dir = Path.Combine(_root, "logs");
                    Directory.CreateDirectory(dir);
                    _logFile = Path.Combine(dir, "launcher.log");
                }
                File.AppendAllText(
                    _logFile,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 入口

        [STAThread]
        private static void Main(string[] args)
        {
            _root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            _autoMode = args.Length > 0 && args[0].Equals("auto", StringComparison.OrdinalIgnoreCase);

            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                MessageBox.Show(
                    "启动失败：\n\n" + ex.Message + "\n\n详情见 logs\\launcher.log",
                    TaskTrayTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void Run(string[] args)
        {
            Log("start, args=" + string.Join(" ", args));

            // 命令行切换开机自启（不需要起服务，也不占用单实例锁）
            if (args.Length > 0)
            {
                string a0 = args[0].ToLowerInvariant();
                if (a0 == "autostart-on" || a0 == "autostart-off")
                {
                    bool on = a0 == "autostart-on";
                    bool ok = SetAutoStart(on);
                    Log((on ? "开启" : "关闭") + "开机自启：" + (ok ? "成功" : "失败"));
                    Environment.ExitCode = ok ? 0 : 1;
                    return;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "AnsCheckinLauncher", out createdNew))
            {
                if (!createdNew)
                {
                    Log("已有实例在运行，唤起界面后退出");
                    OpenConsole();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string node = FindNode();
                if (node == null)
                {
                    Log("未找到 node.exe");
                    MessageBox.Show(
                        "没有找到 Node.js。\n\n请先安装 Node.js（https://nodejs.org），或把 node.exe 加入 PATH 后重试。",
                        TaskTrayTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Log("node = " + node);

                if (!StartServer(node))
                {
                    Log("本地服务启动失败");
                    MessageBox.Show(
                        "本地服务启动失败。\n\n请检查端口是否被占用，或直接运行 app.mjs 查看报错。",
                        TaskTrayTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Log("服务已就绪，port=" + _port);

                BuildTray();
                StartWatcher();
                Log("托盘已就绪");

                // 浏览器统一由服务端打开并校验页面是否真的渲染（见 OpenConsole）。
                // 放到后台线程，避免等待期间托盘/窗口没响应。
                if (!_autoMode) ThreadPool.QueueUserWorkItem(_ => OpenConsole());

                Application.ApplicationExit += (s, e) => StopServer();
                Log("进入消息循环");
                Application.Run();
                Log("退出");
            }
        }

        // ---------------------------------------------------------------- 启动服务

        private static string FindNode()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            string[] guesses =
            {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\nodejs\node.exe"),
            };
            foreach (string g in guesses) if (File.Exists(g)) return g;
            return null;
        }

        private static bool StartServer(string nodeExe)
        {
            string portFile = Path.Combine(_root, ".console-port");
            try { if (File.Exists(portFile)) File.Delete(portFile); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                // 一律由启动器显式请求服务端打开浏览器，避免服务端自己再开一个重复窗口
                Arguments = "app.mjs --no-open",
                WorkingDirectory = _root,
                // 用 shell 分离启动：子进程不继承本进程的 stdio 句柄，
                // 否则会被调用方的管道/控制台挂住，导致 exe 看起来"卡住"。
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                _node = Process.Start(psi);
            }
            catch
            {
                return false;
            }
            if (_node == null) return false;

            // 等待端口文件出现并可用（最多约 20 秒）
            for (int i = 0; i < 100; i++)
            {
                if (_node.HasExited) return false;
                try
                {
                    if (File.Exists(portFile))
                    {
                        int p;
                        if (int.TryParse(File.ReadAllText(portFile).Trim(), out p) && PortAlive(p))
                        {
                            _port = p;
                            return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
            return false;
        }

        private static bool PortAlive(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync("127.0.0.1", port);
                    return task.Wait(700) && client.Connected;
                }
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 打开控制台

        /// <summary>
        /// 打开控制台：让本地服务去开浏览器。
        ///
        /// 为什么不在本进程直接启动浏览器：实测从 GUI 子系统进程里创建 Edge，
        /// 冷启动会立刻以 exitCode 21 退出（窗口不出现）；而从 Node 里 spawn 同一路径
        /// 同一参数则完全正常。所以这件事交给服务端做，这里只发一个 HTTP 请求。
        /// </summary>
        /// <summary>
        /// 把已经打开的控制台窗口切到前台。
        /// 重复双击 exe 时不该再开一个新窗口，直接聚焦原来那个更符合直觉。
        /// </summary>
        private static bool FocusConsoleWindow()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("msedge"))
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        string title = p.MainWindowTitle ?? "";
                        if (!title.Contains(TaskTrayTitle)) continue;

                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        Log("已聚焦已有的控制台窗口 pid=" + p.Id);
                        return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static void OpenConsole()
        {
            if (FocusConsoleWindow()) return;

            string url = "http://127.0.0.1:" + _port + "/";
            Log("请求服务端打开控制台 " + url);

            bool spawned = false;
            bool loaded = false;

            try
            {
                using (var wc = new WebClient())
                {
                    wc.Proxy = null;
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers["Content-Type"] = "application/json";
                    string body = wc.UploadString("http://127.0.0.1:" + _port + "/api/open", "POST", "{}");
                    Log("服务端返回：" + body);
                    spawned = body.Contains("\"ok\":true");
                    loaded = body.Contains("\"loaded\":true");
                }
            }
            catch (Exception ex)
            {
                Log("让服务端打开失败：" + ex.Message);
            }

            if (loaded) return;

            // 没确认到页面渲染就兜底：进程起来了但白屏，或者根本没起来
            string extra = spawned
                ? "控制台窗口已经打开了，但页面没能加载出来。\n\n可以在那个窗口里按 Ctrl+R 刷新，或手动访问："
                : "控制台已在后台运行，但没能自动打开浏览器。\n\n请手动在浏览器里访问：";

            Log(spawned ? "窗口已打开但页面未确认渲染" : "未能打开浏览器，提示用户手动访问");
            try
            {
                MessageBox.Show(
                    extra + "\n" + url,
                    TaskTrayTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private static void RunCheckin()
        {
            string node = FindNode();
            if (node == null) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = node,
                    Arguments = "checkin.mjs",
                    WorkingDirectory = _root,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);
                Notify("开始执行签到", "正在后台执行，完成后会提示结果。", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Notify("启动失败", ex.Message, ToolTipIcon.Error);
            }
        }

        // ---------------------------------------------------------------- 托盘

        private static void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Text = TaskTrayTitle,
                Visible = true,
            };

            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _tray.Icon = icon ?? SystemIcons.Application;
            }
            catch { _tray.Icon = SystemIcons.Application; }

            var menu = new ContextMenuStrip();

            var open = new ToolStripMenuItem("打开控制台");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += (s, e) => OpenConsole();

            var run = new ToolStripMenuItem("立即签到");
            run.Click += (s, e) => RunCheckin();

            var folder = new ToolStripMenuItem("打开数据文件夹");
            folder.Click += (s, e) => { try { Process.Start(_root); } catch { } };

            var logFolder = new ToolStripMenuItem("打开日志文件夹");
            logFolder.Click += (s, e) =>
            {
                try
                {
                    string dir = Path.Combine(_root, "logs");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Process.Start(dir);
                }
                catch { }
            };

            var autoStart = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            autoStart.Checked = HasAutoStart();
            autoStart.CheckedChanged += (s, e) => SetAutoStart(autoStart.Checked);

            var quit = new ToolStripMenuItem("退出");
            quit.Click += (s, e) => { _exiting = true; _tray.Visible = false; Application.Exit(); };

            menu.Items.Add(open);
            menu.Items.Add(run);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(folder);
            menu.Items.Add(logFolder);
            menu.Items.Add(autoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => OpenConsole();
        }

        private static void Notify(string title, string text, ToolTipIcon icon)
        {
            try
            {
                if (_tray == null) return;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 监听执行结果

        private static void StartWatcher()
        {
            string runsFile = Path.Combine(_root, "logs", "runs.jsonl");
            _runsOffset = File.Exists(runsFile) ? new FileInfo(runsFile).Length : 0;

            _watch = new System.Windows.Forms.Timer { Interval = 3000 };
            _watch.Tick += (s, e) =>
            {
                if (_exiting) return;
                try
                {
                    if (!File.Exists(runsFile)) return;
                    long len = new FileInfo(runsFile).Length;
                    if (len <= _runsOffset) { if (len < _runsOffset) _runsOffset = 0; return; }

                    string text;
                    using (var fs = new FileStream(runsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Seek(_runsOffset, SeekOrigin.Begin);
                        using (var reader = new StreamReader(fs, Encoding.UTF8))
                        {
                            text = reader.ReadToEnd();
                        }
                    }
                    _runsOffset = len;

                    foreach (string line in text.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        Announce(t);
                    }
                }
                catch { }
            };
            _watch.Start();
        }

        /// <summary>从一行 runs.jsonl 里提取摘要并弹通知（不引第三方 JSON 库，手工取字段）。</summary>
        private static void Announce(string jsonLine)
        {
            int granted = (int)ExtractNumber(jsonLine, "\"totalGranted\":");
            int accountCount = (int)ExtractNumber(jsonLine, "\"accountCount\":");
            bool ok = !jsonLine.Contains("\"ok\":false");
            string date = ExtractString(jsonLine, "\"date\":\"");

            // 多账号记录里：expired / failed 是数组，空数组即全部正常
            int expiredCount = CountArrayItems(jsonLine, "\"expired\":[");
            int failedCount = CountArrayItems(jsonLine, "\"failed\":[");
            string who = accountCount > 1 ? accountCount + " 个账号" : "";
            string when = date.Length > 0 ? date + " " : "";

            string title;
            string body;
            ToolTipIcon icon;

            if (expiredCount > 0)
            {
                title = "有 " + expiredCount + " 个账号需要重新登录";
                body = when + "其余账号已正常执行。双击托盘图标打开控制台，点「+ 添加账号」重新登录。";
                icon = ToolTipIcon.Warning;
            }
            else if (failedCount > 0 || !ok)
            {
                title = "签到部分失败";
                body = when + (who.Length > 0 ? who + "，" : "") + "本次领取 " + granted + " 积分，有任务失败。可在控制台查看日志。";
                icon = ToolTipIcon.Warning;
            }
            else if (granted > 0)
            {
                title = "签到成功 +" + granted + " 积分";
                body = when + (who.Length > 0 ? who + "，" : "") + "今日任务已自动领取完成。";
                icon = ToolTipIcon.Info;
            }
            else
            {
                title = "签到已执行";
                body = when + (who.Length > 0 ? who + "，" : "") + "本次没有新增积分（可能已领过或站点接口拒绝）。";
                icon = ToolTipIcon.Info;
            }

            Notify(title, body, icon);
        }

        /// <summary>数一下 "key":[ ... ] 里有几个字符串元素</summary>
        private static int CountArrayItems(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            int end = line.IndexOf(']', i);
            if (end < 0) return 0;
            string inner = line.Substring(i + key.Length, end - i - key.Length);
            int count = 0;
            bool inString = false;
            foreach (char c in inner)
            {
                if (c == '"')
                {
                    if (inString) count++;
                    inString = !inString;
                }
            }
            return count / 2;
        }

        private static double ExtractNumber(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int start = i;
            while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '-')) i++;
            double v;
            return double.TryParse(line.Substring(start, i - start), out v) ? v : 0;
        }

        private static string ExtractString(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            i += key.Length;
            int end = line.IndexOf('"', i);
            if (end < 0) return "";
            return line.Substring(i, end - i);
        }

        // ---------------------------------------------------------------- 开机自启

        private static string StartupDir()
        {
            // 测试钩子：把"启动文件夹"重定向到别处，用于在受限环境里验证创建逻辑。
            // 正常使用不会设置这个变量。
            string overrideDir = Environment.GetEnvironmentVariable("ANS_STARTUP_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        private static string StartupLnk()
        {
            return Path.Combine(StartupDir(), AutoStartLnkName);
        }

        private static string StartupCmd()
        {
            return Path.Combine(StartupDir(), "ANS每日签到.cmd");
        }

        private static bool HasAutoStart()
        {
            try { return File.Exists(StartupLnk()) || File.Exists(StartupCmd()); } catch { return false; }
        }

        private static bool SetAutoStart(bool on)
        {
            try
            {
                string dir = StartupDir();
                Directory.CreateDirectory(dir);

                if (on)
                {
                    string err;
                    string lnk = StartupLnk();

                    if (CreateStartupShortcut(lnk, out err))
                    {
                        // 有了快捷方式就清掉可能存在的 .cmd 兜底
                        try { File.Delete(StartupCmd()); } catch { }
                        Notify("已设置开机自启", "下次登录系统后会自动在托盘运行。", ToolTipIcon.Info);
                        return true;
                    }

                    Log("创建快捷方式失败，改用 .cmd 兜底：" + err);
                    if (CreateStartupCmd(out err))
                    {
                        Notify("已设置开机自启", "下次登录系统后会自动在托盘运行（使用脚本方式）。", ToolTipIcon.Info);
                        return true;
                    }

                    Notify("设置失败", err, ToolTipIcon.Error);
                    return false;
                }

                if (File.Exists(StartupLnk())) File.Delete(StartupLnk());
                if (File.Exists(StartupCmd())) File.Delete(StartupCmd());
                Notify("已取消开机自启", "", ToolTipIcon.Info);
                return true;
            }
            catch (Exception ex)
            {
                Log("设置开机自启异常：" + ex);
                Notify("设置失败", ex.Message, ToolTipIcon.Error);
                return false;
            }
        }

        /// <summary>兜底方案：在启动文件夹放一个 .cmd，登录时会执行它</summary>
        private static bool CreateStartupCmd(out string error)
        {
            error = "";
            try
            {
                string exe = Application.ExecutablePath;
                string content =
                    "@echo off\r\n" +
                    "start \"\" \"" + exe + "\" auto\r\n" +
                    "exit\r\n";
                File.WriteAllText(StartupCmd(), content, new UTF8Encoding(false));
                Log("已创建开机自启脚本：" + StartupCmd());
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 创建启动文件夹里的快捷方式。
        ///
        /// 直接在本进程里用 WScript.Shell 的 COM 会失败（报"无法保存快捷方式"），
        /// 但同样的 COM 调用在 PowerShell 里正常。所以这里改为：
        /// 写一个临时 .ps1 交给 powershell.exe 执行，再由本进程校验结果。
        /// 全程用脚本文件而非内联命令，避免引号与中文路径的转义问题。
        /// </summary>
        private static bool CreateStartupShortcut(string lnk, out string error)
        {
            error = "";
            string exe = Application.ExecutablePath;
            string workDir = Path.GetDirectoryName(exe) ?? _root;
            // 临时脚本放在程序自己的 logs 目录，避免系统临时目录的权限问题
            string logDir = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logDir);
            string ps1 = Path.Combine(logDir, "make-shortcut.ps1");

            string script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$lnk = " + PsSingleQuote(lnk) + "\r\n" +
                "$sh = New-Object -ComObject WScript.Shell\r\n" +
                "$sc = $sh.CreateShortcut($lnk)\r\n" +
                "$sc.TargetPath = " + PsSingleQuote(exe) + "\r\n" +
                "$sc.Arguments = 'auto'\r\n" +
                "$sc.WorkingDirectory = " + PsSingleQuote(workDir) + "\r\n" +
                "$sc.IconLocation = " + PsSingleQuote(exe + ",0") + "\r\n" +
                "$sc.Description = 'ANS 每日签到（后台托盘）'\r\n" +
                "$sc.Save()\r\n";

            try
            {
                File.WriteAllText(ps1, script, new UTF8Encoding(true));

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + ps1 + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var p = Process.Start(psi))
                {
                    string stderr = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        error = "创建快捷方式超时";
                        return false;
                    }

                    if (File.Exists(lnk))
                    {
                        Log("已创建开机自启快捷方式：" + lnk);
                        return true;
                    }

                    error = string.IsNullOrWhiteSpace(stderr)
                        ? "快捷方式没有生成，可能是启动文件夹不可写"
                        : stderr.Trim();
                    Log("创建快捷方式失败：" + error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log("创建快捷方式异常：" + ex);
                return false;
            }
            finally
            {
                try { File.Delete(ps1); } catch { }
            }
        }

        /// <summary>PowerShell 单引号字符串转义（内部单引号翻倍）</summary>
        private static string PsSingleQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        // ---------------------------------------------------------------- 退出

        private static void StopServer()
        {
            try
            {
                if (_watch != null) _watch.Stop();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_node != null && !_node.HasExited)
                {
                    _node.Kill();
                    _node.WaitForExit(3000);
                }
            }
            catch { }
        }
    }
}
