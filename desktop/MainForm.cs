using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AnsCheckin
{
    /// <summary>
    /// 原生控制台窗口。
    ///
    /// 刻意不内嵌浏览器：整个界面用 WinForms 画，数据通过本地 HTTP 接口取。
    /// 这样应用彻底不碰用户的浏览器，也就不会出现"Edge 实例权限冲突"那类弹窗。
    ///
    /// DPI 处理：清单文件声明了 DPI 感知（Windows 不再按位图拉伸，文字才清晰），
    /// 布局尺寸则统一乘 _s（=系统 DPI/96）。字体用磅值会自动跟随 DPI 放大，
    /// 所以只要把像素尺寸一起放大，比例就对了。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---- 配色（与网页版一致）----
        private static readonly Color Bg = Color.FromArgb(242, 243, 246);
        private static readonly Color Ink = Color.FromArgb(16, 21, 41);
        private static readonly Color Ink2 = Color.FromArgb(90, 99, 122);
        private static readonly Color Muted = Color.FromArgb(152, 162, 179);
        private static readonly Color Brand = Color.FromArgb(91, 91, 246);
        private static readonly Color Ok = Color.FromArgb(5, 150, 105);
        private static readonly Color Warn = Color.FromArgb(180, 83, 9);
        private static readonly Color Bad = Color.FromArgb(220, 38, 38);

        private readonly int _port;
        private readonly string _root;
        private readonly float _s = 1F;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private readonly Font _fTitle = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        private readonly Font _fSlogan = new Font("Microsoft YaHei UI", 9F);
        private readonly Font _fNum = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold);
        private readonly Font _fCaption = new Font("Microsoft YaHei UI", 8.5F);
        private readonly Font _fBody = new Font("Microsoft YaHei UI", 9.5F);
        private readonly Font _fSmall = new Font("Microsoft YaHei UI", 8.5F);

        private Label _statusPill;
        private Label _statAccounts;
        private Label _statPoints;
        private Label _statToday;
        private Label _statLast;
        private Panel _rowHost;
        private TextBox _log;
        private CheckBox _cbCheckin;
        private CheckBox _cbRead;
        private CheckBox _cbStar;
        private CheckBox _cbFeed;
        private TextBox _times;
        private RoundButton _btnRun;
        private RoundButton _btnRunOne;
        private Label _accountsCaption;
        private FlowLayoutPanel _rightCol;
        private CardPanel _chartCard;
        private CardPanel _runsCard;
        private ComboBox _chartAcc;
        private SparkChart _chart;
        private ListView _runsList;
        private readonly Dictionary<string, bool> _collapsed = new Dictionary<string, bool>();
        private readonly Dictionary<string, Action> _cardApply = new Dictionary<string, Action>();
        private string _chartAccountId = "";
        private Dictionary<string, object> _lastRoot;

        // ---------------------------------------------------------------- 可折叠卡片

        private CardPanel MakeCollapsible(string title, string key, int expandedHeight, Control headerRight)
        {
            var card = new CardPanel
            {
                Width = Px(366),
                Height = expandedHeight,
                Margin = new Padding(0, 0, 0, Px(12)),
                Padding = new Padding(Px(12), Px(4), Px(12), Px(10)),
            };

            var head = new Panel
            {
                Dock = DockStyle.Top,
                Height = Px(34),
                BackColor = Color.White,
                Cursor = Cursors.Hand,
            };
            var titleLabel = new Label
            {
                Text = title,
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Left = 0,
                Top = Px(5),
                Cursor = Cursors.Hand,
            };
            var chev = new Label
            {
                Text = "▾",
                Font = new Font("Microsoft YaHei UI", 10F),
                ForeColor = Muted,
                AutoSize = false,
                Width = Px(28),
                Height = Px(28),
                Top = 0,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
            };
            head.Controls.Add(titleLabel);
            head.Controls.Add(chev);
            if (headerRight != null)
            {
                headerRight.Top = Px(3);
                head.Controls.Add(headerRight);
            }
            head.Resize += (s, e) =>
            {
                chev.Left = Math.Max(0, head.Width - chev.Width);
                if (headerRight != null)
                {
                    headerRight.Left = Math.Max(0, head.Width - chev.Width - headerRight.Width - Px(6));
                }
            };

            card.Controls.Add(head);

            Action apply = () =>
            {
                bool col = _collapsed.ContainsKey(key) && _collapsed[key];
                card.Height = col ? Px(46) : expandedHeight;
                foreach (Control c in card.Controls)
                {
                    if (c != head) c.Visible = !col;
                }
                chev.Text = col ? "▸" : "▾";
            };

            EventHandler onclick = (s, e) =>
            {
                bool col = _collapsed.ContainsKey(key) && _collapsed[key];
                _collapsed[key] = !col;
                apply();
                SaveCollapsed();
            };
            head.Click += onclick;
            titleLabel.Click += onclick;
            chev.Click += onclick;

            _cardApply[key] = apply;
            return card;
        }

        private void SaveCollapsed()
        {
            try
            {
                File.WriteAllText(Path.Combine(_root, "desktop-ui.json"), _json.Serialize(_collapsed));
            }
            catch { }
        }

        private void LoadCollapsed()
        {
            try
            {
                string path = Path.Combine(_root, "desktop-ui.json");
                if (!File.Exists(path)) return;
                var d = _json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (d == null) return;
                foreach (var kv in d)
                {
                    _collapsed[kv.Key] = Convert.ToBoolean(kv.Value);
                }
            }
            catch { }
        }
        private RoundButton _btnSaveQuests;
        private RoundButton _btnSaveTimes;
        private System.Windows.Forms.Timer _timer;
        private bool _busy;
        private string _selectedId;
        private readonly List<AccountRow> _rowViews = new List<AccountRow>();

        // GDI 的 Graphics.DpiX 在系统级 DPI 感知进程里常常固定返回 96，
        // 必须用 GetDpiForSystem 才能拿到真实缩放比例。
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private int Px(int v)
        {
            return (int)Math.Round(v * _s);
        }

        private void DiagLog(string message)
        {
            try
            {
                string dir = Path.Combine(_root, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "ui.log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }

        public MainForm(int port, string root)
        {
            _port = port;
            _root = root;

            try
            {
                uint dpi = GetDpiForSystem();
                _s = dpi > 0 ? dpi / 96F : 1F;
                DiagLog("GetDpiForSystem=" + dpi + " scale=" + _s);
            }
            catch (Exception ex)
            {
                _s = 1F;
                DiagLog("GetDpiForSystem 失败：" + ex.Message);
            }
            if (_s < 1F) _s = 1F;

            Text = "ANS 每日签到 · 控制台";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Px(1020), Px(900));
            MinimumSize = new Size(Px(880), Px(720));
            BackColor = Bg;
            Font = _fBody;
            DoubleBuffered = true;

            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) Icon = ico;
            }
            catch { }

            BuildUi();
            LoadCollapsed();
            if (_cardApply.ContainsKey("chart")) _cardApply["chart"]();
            if (_cardApply.ContainsKey("runs")) _cardApply["runs"]();
            DiagLog("client=" + ClientSize.Width + "x" + ClientSize.Height +
                    " min=" + MinimumSize.Width + "x" + MinimumSize.Height);

            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (s, e) =>
            {
                RefreshStatus();
                if (!_log.Focused) RefreshLog();
            };
            _timer.Start();

            Load += (s, e) =>
            {
                DiagLog("onLoad client=" + ClientSize.Width + "x" + ClientSize.Height);
                RefreshStatus();
                RefreshLog();
            };
        }

        // ---------------------------------------------------------------- 界面

        private void BuildUi()
        {
            // ===== 顶部标题 =====
            var header = new Panel { Dock = DockStyle.Top, Height = Px(92), BackColor = Bg };
            var logo = new PictureBox
            {
                Left = Px(26),
                Top = Px(16),
                Width = Px(50),
                Height = Px(50),
                SizeMode = PictureBoxSizeMode.Zoom,
            };
            try
            {
                string logoPath = Path.Combine(_root, "desktop", "logo-64.png");
                if (File.Exists(logoPath))
                {
                    using (var fs = new FileStream(logoPath, FileMode.Open, FileAccess.Read))
                    {
                        logo.Image = Image.FromStream(fs);
                    }
                }
                else
                {
                    Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (ico != null) logo.Image = ico.ToBitmap();
                }
            }
            catch { }
            header.Controls.Add(logo);

            header.Controls.Add(new Label
            {
                Text = "每日签到控制台",
                Font = _fTitle,
                ForeColor = Ink,
                AutoSize = true,
                Left = Px(88),
                Top = Px(16),
            });

            header.Controls.Add(new Label
            {
                Text = "老实签到，很麻烦吧",
                Font = _fSlogan,
                ForeColor = Brand,
                AutoSize = true,
                Left = Px(90),
                Top = Px(50),
            });

            _statusPill = new PillLabel
            {
                AutoSize = false,
                Height = Px(30),
                Width = Px(190),
                Top = Px(28),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            };
            header.Controls.Add(_statusPill);
            header.Resize += (s, e) => { _statusPill.Left = header.Width - _statusPill.Width - Px(26); };

            // ===== 统计卡片 =====
            var stats = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Px(96),
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Bg,
                Padding = new Padding(Px(20), 0, Px(20), 0),
            };
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _statAccounts = AddStatCard(stats, 0, "账号数");
            _statPoints = AddStatCard(stats, 1, "总积分");
            _statToday = AddStatCard(stats, 2, "今日已领");
            _statLast = AddStatCard(stats, 3, "上次执行");

            // ===== 工具栏 =====
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Px(56),
                BackColor = Bg,
                Padding = new Padding(Px(26), Px(11), 0, 0),
                WrapContents = false,
            };
            int gap = Px(10);
            _btnRun = new RoundButton { Text = "全部执行", Width = Px(104), Primary = true, Margin = new Padding(0, 0, gap, 0) };
            _btnRunOne = new RoundButton { Text = "执行选中", Width = Px(96), Margin = new Padding(0, 0, gap, 0) };
            var btnToggle = new RoundButton { Text = "停用 / 启用", Width = Px(108), Margin = new Padding(0, 0, gap, 0) };
            var btnDelete = new RoundButton { Text = "删除账号", Width = Px(92), Danger = true, Margin = new Padding(0, 0, gap, 0) };
            var btnAdd = new RoundButton { Text = "添加账号", Width = Px(92), Margin = new Padding(0, 0, gap, 0) };
            var btnRefresh = new RoundButton { Text = "刷新", Width = Px(72), Margin = new Padding(0) };
            toolbar.Controls.AddRange(new Control[] { _btnRun, _btnRunOne, btnToggle, btnDelete, btnAdd, btnRefresh });

            _btnRun.Click += (s, e) => RunCheckin(null);
            _btnRunOne.Click += (s, e) => RunCheckin(_selectedId);
            btnToggle.Click += (s, e) => ToggleAccount();
            btnDelete.Click += (s, e) => DeleteAccount();
            btnAdd.Click += (s, e) => AddAccount();
            btnRefresh.Click += (s, e) => { RefreshStatus(); RefreshLog(); };

            // ===== 账号卡片 =====
            var accountsCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Px(4)) };

            var captionBar = new Panel { Dock = DockStyle.Top, Height = Px(42), BackColor = Color.White };
            _accountsCaption = new Label
            {
                Text = "账号",
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Left = Px(16),
                Top = Px(9),
            };
            captionBar.Controls.Add(_accountsCaption);

            _rowHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            accountsCard.Controls.Add(_rowHost);
            accountsCard.Controls.Add(captionBar);

            var accountsWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(26), 0, Px(26), Px(8)), BackColor = Bg };
            accountsWrap.Controls.Add(accountsCard);

            // ===== 右栏：积分历史 + 最近执行记录 =====
            _rightCol = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = Px(392),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Bg,
                Padding = new Padding(0, 0, Px(26), Px(8)),
            };

            _chartAcc = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = Px(140),
                Font = new Font("Microsoft YaHei UI", 8.5F),
            };
            _chartAcc.SelectedIndexChanged += (s, e) => RefreshChart();

            _chartCard = MakeCollapsible("积分历史", "chart", Px(178), _chartAcc);

            _chart = new SparkChart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
            };
            _chartCard.Controls.Add(_chart);
            _chartCard.Controls.SetChildIndex(_chart, 0);
            _rightCol.Controls.Add(_chartCard);

            _runsCard = MakeCollapsible("最近执行", "runs", Px(168), null);
            _runsList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                BackColor = Color.White,
            };
            _runsList.Columns.Add("时间", Px(84));
            _runsList.Columns.Add("账号", Px(90));
            _runsList.Columns.Add("领取", Px(46), HorizontalAlignment.Right);
            _runsList.Columns.Add("结果", Px(70));
            _runsCard.Controls.Add(_runsList);
            _rightCol.Controls.Add(_runsCard);

            var main = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            main.Controls.Add(accountsWrap);
            main.Controls.Add(_rightCol);

            // ===== 设置卡片 =====
            var settingsCard = new CardPanel
            {
                Dock = DockStyle.Bottom,
                Height = Px(112),
                Padding = new Padding(Px(14), Px(8), Px(14), Px(8)),
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.White,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(84)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            grid.Controls.Add(new Label
            {
                Text = "自动任务",
                Font = _fCaption,
                ForeColor = Muted,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(Px(2), 0, 0, 0),
            }, 0, 0);

            var questFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = Color.White,
                Margin = new Padding(0),
            };
            _cbCheckin = MakeCheck(questFlow, "每日签到");
            _cbRead = MakeCheck(questFlow, "VibeHub 阅读");
            _cbStar = MakeCheck(questFlow, "点亮 Star");
            _cbFeed = MakeCheck(questFlow, "投喂小恐龙");
            _btnSaveQuests = new RoundButton
            {
                Text = "保存",
                Width = Px(64),
                Height = Px(28),
                Margin = new Padding(Px(6), Px(2), Px(8), 0),
            };
            _btnSaveQuests.Click += (s, e) => SaveQuests();
            questFlow.Controls.Add(_btnSaveQuests);
            grid.Controls.Add(questFlow, 1, 0);

            grid.Controls.Add(new Label
            {
                Text = "执行时间",
                Font = _fCaption,
                ForeColor = Muted,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(Px(2), 0, 0, 0),
            }, 0, 1);

            var timeFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = Color.White,
                Margin = new Padding(0),
            };
            _times = new TextBox
            {
                Width = Px(200),
                Font = new Font("Consolas", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, Px(4), Px(8), 0),
            };
            timeFlow.Controls.Add(_times);
            _btnSaveTimes = new RoundButton
            {
                Text = "保存",
                Width = Px(64),
                Height = Px(28),
                Margin = new Padding(0, Px(2), Px(10), 0),
            };
            _btnSaveTimes.Click += (s, e) => SaveTimes();
            timeFlow.Controls.Add(_btnSaveTimes);
            timeFlow.Controls.Add(new Label
            {
                Text = "（逗号分隔，例如 12:00, 21:30）",
                Font = _fSmall,
                ForeColor = Muted,
                AutoSize = true,
                Margin = new Padding(Px(8), Px(9), 0, 0),
            });
            grid.Controls.Add(timeFlow, 1, 1);

            settingsCard.Controls.Add(grid);

            var settingsWrap = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Px(124),
                Padding = new Padding(Px(26), Px(6), Px(26), Px(6)),
                BackColor = Bg,
            };
            settingsWrap.Controls.Add(settingsCard);

            // ===== 日志卡片 =====
            var logCard = new CardPanel
            {
                Dock = DockStyle.Bottom,
                Height = Px(176),
                Padding = new Padding(Px(14), Px(8), Px(14), Px(12)),
            };
            var logTitle = new Label
            {
                Text = "执行日志",
                Font = _fCaption,
                ForeColor = Muted,
                Dock = DockStyle.Top,
                Height = Px(22),
            };
            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(12, 15, 23),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 9.5F),
                BorderStyle = BorderStyle.None,
            };
            logCard.Controls.Add(_log);
            logCard.Controls.Add(logTitle);

            var logWrap = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Px(192),
                Padding = new Padding(Px(26), Px(4), Px(26), Px(10)),
                BackColor = Bg,
            };
            logWrap.Controls.Add(logCard);

            // 停靠顺序：Fill 先加，底部按"日志在最下"的顺序加
            Controls.Add(main);
            Controls.Add(settingsWrap);
            Controls.Add(logWrap);
            Controls.Add(toolbar);
            Controls.Add(stats);
            Controls.Add(header);
        }

        private Label AddStatCard(TableLayoutPanel host, int col, string caption)
        {
            var card = new CardPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(col == 0 ? 0 : Px(6), 0, col == 3 ? 0 : Px(6), Px(10)),
            };
            card.Controls.Add(new Label
            {
                Text = caption,
                Font = _fCaption,
                ForeColor = Muted,
                AutoSize = true,
                Left = Px(14),
                Top = Px(10),
            });
            var val = new Label
            {
                Text = "—",
                Font = _fNum,
                ForeColor = Ink,
                AutoSize = true,
                Left = Px(14),
                Top = Px(30),
            };
            card.Controls.Add(val);
            host.Controls.Add(card, col, 0);
            return val;
        }

        private CheckBox MakeCheck(Control parent, string text)
        {
            var cb = new CheckBox
            {
                Text = text,
                AutoSize = true,
                ForeColor = Ink,
                Font = _fBody,
                Margin = new Padding(0, Px(3), Px(14), 0),
            };
            parent.Controls.Add(cb);
            return cb;
        }

        // ---------------------------------------------------------------- HTTP

        private string ApiGet(string path)
        {
            using (var wc = new WebClient())
            {
                wc.Proxy = null;
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString("http://127.0.0.1:" + _port + path);
            }
        }

        private string ApiSend(string path, string method, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _port + path);
            req.Proxy = null;
            req.Method = method;
            req.ContentType = "application/json";
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "{}");
            req.ContentLength = payload.Length;
            using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    throw new Exception(ExtractError(text) ?? text);
                }
            }
        }

        private static string ExtractError(string json)
        {
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("error")) return Convert.ToString(d["error"]);
            }
            catch { }
            return null;
        }

        private string ApiPost(string path, string body) { return ApiSend(path, "POST", body); }
        private string ApiPatch(string path, string body) { return ApiSend(path, "PATCH", body); }
        private string ApiDelete(string path) { return ApiSend(path, "DELETE", "{}"); }

        private static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object[] AsList(object o)
        {
            return o as object[] ?? new object[0];
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- 刷新

        private void RefreshStatus()
        {
            try
            {
                var root = AsDict(_json.DeserializeObject(ApiGet("/api/status")));
                _lastRoot = root;
                var summary = AsDict(root.ContainsKey("summary") ? root["summary"] : null);
                var today = AsDict(root.ContainsKey("todaySummary") ? root["todaySummary"] : null);

                int total = (int)ToDouble(summary, "total");
                int needLogin = (int)ToDouble(summary, "needLogin");
                double points = ToDouble(summary, "totalPoints");
                double granted = ToDouble(today, "totalGranted");
                string lastAt = Str(today, "lastRunAt");

                _statAccounts.Text = total.ToString();
                _statPoints.Text = points.ToString("0.##");
                _statToday.Text = granted > 0 ? "+" + granted.ToString("0.##") : "0";
                _statLast.Text = string.IsNullOrEmpty(lastAt) ? "—" : ShortTime(lastAt);

                if (total == 0) SetPill("还没有账号", Ink2, Color.FromArgb(240, 241, 246));
                else if (needLogin > 0) SetPill(needLogin + " 个账号需重新登录", Warn, Color.FromArgb(255, 248, 232));
                else SetPill(total + " 个账号正常", Ok, Color.FromArgb(233, 251, 243));

                _accountsCaption.Text = "账号   共 " + total + " 个";

                _rowHost.SuspendLayout();
                while (_rowHost.Controls.Count > 0)
                {
                    Control old = _rowHost.Controls[0];
                    _rowHost.Controls.RemoveAt(0);
                    old.Dispose();
                }
                _rowViews.Clear();

                var rows = new List<AccountRow>();
                foreach (object item in AsList(root.ContainsKey("accounts") ? root["accounts"] : null))
                {
                    var a = AsDict(item);
                    var t = AsDict(a.ContainsKey("today") ? a["today"] : null);
                    string id = Str(a, "id");

                    var row = new AccountRow
                    {
                        AccountId = id,
                        Email = Str(a, "email"),
                        Nickname = Str(a, "label"),
                        Points = a.ContainsKey("points") && a["points"] != null ? Convert.ToString(a["points"]) : "—",
                        StatusKind = Str(t, "checkin"),
                        StatusText = CheckinText(Str(t, "checkin")),
                        LastText = string.IsNullOrEmpty(Str(t, "lastRunAt")) ? "—" : ShortTime(Str(t, "lastRunAt")),
                        Extra = a.ContainsKey("needsLogin") && Convert.ToBoolean(a["needsLogin"]) ? "需登录"
                              : (a.ContainsKey("enabled") && !Convert.ToBoolean(a["enabled"]) ? "已停用" : ""),
                        Selected = id == _selectedId,
                    };
                    row.Click += OnRowClick;
                    rows.Add(row);
                    _rowViews.Add(row);
                }
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var r = rows[i];
                    r.Dock = DockStyle.Top;
                    r.Height = Px(66);
                    _rowHost.Controls.Add(r);
                    _rowHost.Controls.SetChildIndex(r, 0);
                }
                if (rows.Count == 0)
                {
                    _rowHost.Controls.Add(new Label
                    {
                        Text = "还没有账号，点上方「添加账号」开始。",
                        Dock = DockStyle.Top,
                        Height = Px(70),
                        TextAlign = ContentAlignment.MiddleCenter,
                        ForeColor = Muted,
                    });
                }
                _rowHost.ResumeLayout();

                var cfg = AsDict(root.ContainsKey("config") ? root["config"] : null);
                var quests = new HashSet<string>();
                foreach (object q in AsList(cfg.ContainsKey("quests") ? cfg["quests"] : null))
                {
                    if (q != null) quests.Add(Convert.ToString(q));
                }
                if (!_cbCheckin.Focused) _cbCheckin.Checked = quests.Contains("daily_checkin");
                if (!_cbRead.Focused) _cbRead.Checked = quests.Contains("daily_vibehub_read");
                if (!_cbStar.Focused) _cbStar.Checked = quests.Contains("daily_explore_star");
                if (!_cbFeed.Focused) _cbFeed.Checked = quests.Contains("daily_agent_feed");

                if (!_times.Focused)
                {
                    var parts = new List<string>();
                    foreach (object v in AsList(cfg.ContainsKey("schedule") ? cfg["schedule"] : null))
                    {
                        if (v != null) parts.Add(Convert.ToString(v));
                    }
                    string joined = string.Join(", ", parts.ToArray());
                    if (_times.Text != joined) _times.Text = joined;
                }

                SyncChartAccounts(root);
                RefreshChart();
                RefreshRuns(root);
            }
            catch (Exception ex)
            {
                SetPill("服务未就绪", Bad, Color.FromArgb(254, 242, 242));
                if (_log != null) _log.Text = "无法读取状态：" + ex.Message + Environment.NewLine + _log.Text;
            }
        }

        private void OnRowClick(object sender, EventArgs e)
        {
            var row = sender as AccountRow;
            if (row == null) return;
            _selectedId = row.AccountId;
            foreach (var r in _rowViews)
            {
                r.Selected = r.AccountId == _selectedId;
                r.Invalidate();
            }
        }

        private void SetPill(string text, Color fore, Color back)
        {
            _statusPill.Text = text;
            _statusPill.ForeColor = fore;
            _statusPill.BackColor = back;
        }

        private static string CheckinText(string kind)
        {
            switch (kind)
            {
                case "granted": return "已签到";
                case "duplicate": return "今日已领过";
                case "rejected": return "服务端拒绝";
                case "noop": return "未加分";
                case "error": return "失败";
                default: return "今日未签到";
            }
        }

        private static string ShortTime(string iso)
        {
            DateTime dt;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
            {
                return dt.ToLocalTime().ToString("MM-dd HH:mm");
            }
            return iso;
        }

        private static double ToDouble(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            double r;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
        }

        private void RefreshLog()
        {
            try
            {
                var root = AsDict(_json.DeserializeObject(ApiGet("/api/logs")));
                var lines = AsList(root.ContainsKey("lines") ? root["lines"] : null);
                var sb = new StringBuilder();
                int start = Math.Max(0, lines.Length - 100);
                for (int i = start; i < lines.Length; i++) sb.AppendLine(Convert.ToString(lines[i]));
                string text = sb.ToString();
                if (_log.Text != text)
                {
                    _log.Text = text;
                    ScrollLogToEnd();
                }
            }
            catch { }
        }

        // ScrollToCaret 在多行只读框里常常不生效，直接用 Win32 消息把插入点移到末尾再滚动
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int EM_SETSEL = 0x00B1;
        private const int EM_SCROLLCARET = 0x00B7;
        private const int EM_LINESCROLL = 0x00B6;

        private void ScrollLogToEnd()
        {
            try
            {
                if (!_log.IsHandleCreated) return;
                int len = _log.TextLength;
                SendMessage(_log.Handle, EM_SETSEL, (IntPtr)len, (IntPtr)len);
                SendMessage(_log.Handle, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
                // 只读框里 ScrollCaret 时好时坏，再补一刀"向下滚很多行"，
                // 行数超出部分会被系统自动夹到末尾。
                SendMessage(_log.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)10000);
            }
            catch { }
        }

        private void SyncChartAccounts(Dictionary<string, object> root)
        {
            var accounts = AsList(root.ContainsKey("accounts") ? root["accounts"] : null);

            // 只在账号集合变化时重建下拉，避免每 3 秒把用户的选择冲掉
            var ids = new List<string>();
            foreach (object item in accounts) ids.Add(Str(AsDict(item), "id"));
            var current = new List<string>();
            foreach (object it in _chartAcc.Items) current.Add(((ComboItem)it).Id);
            if (string.Join("|", ids.ToArray()) == string.Join("|", current.ToArray())) return;

            _chartAcc.BeginUpdate();
            _chartAcc.Items.Clear();
            foreach (object item in accounts)
            {
                var a = AsDict(item);
                string label = Str(a, "label");
                if (label.Length == 0) label = Str(a, "email");
                _chartAcc.Items.Add(new ComboItem { Id = Str(a, "id"), Text = label });
            }
            _chartAcc.EndUpdate();

            if (_chartAccountId.Length == 0 && _chartAcc.Items.Count > 0)
            {
                _chartAccountId = ids[0];
            }
            for (int i = 0; i < _chartAcc.Items.Count; i++)
            {
                if (((ComboItem)_chartAcc.Items[i]).Id == _chartAccountId)
                {
                    _chartAcc.SelectedIndex = i;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- 图表 / 执行记录

        private sealed class ComboItem
        {
            public string Id = "";
            public string Text = "";
            public override string ToString() { return Text; }
        }

        private void RefreshChart()
        {
            if (_lastRoot == null) return;
            var item = _chartAcc.SelectedItem as ComboItem;
            if (item != null) _chartAccountId = item.Id;

            var runs = AsList(_lastRoot.ContainsKey("recent") ? _lastRoot["recent"] : null);
            var pts = new List<double>();
            foreach (object r in runs)
            {
                var run = AsDict(r);
                foreach (object a in AsList(run.ContainsKey("accounts") ? run["accounts"] : null))
                {
                    var ad = AsDict(a);
                    if (Str(ad, "id") != _chartAccountId) continue;
                    if (ad.ContainsKey("points1") && ad["points1"] != null) pts.Add(ToDouble(ad, "points1"));
                }
            }
            if (pts.Count > 30) pts = pts.GetRange(pts.Count - 30, 30);
            _chart.Series = pts.ToArray();
            _chart.Invalidate();
        }

        private void RefreshRuns(Dictionary<string, object> root)
        {
            var runs = AsList(root.ContainsKey("recent") ? root["recent"] : null);
            _runsList.BeginUpdate();
            _runsList.Items.Clear();
            int shown = 0;
            for (int i = runs.Length - 1; i >= 0 && shown < 14; i--, shown++)
            {
                var r = AsDict(runs[i]);
                var accs = AsList(r.ContainsKey("accounts") ? r["accounts"] : null);

                double granted = 0;
                string who = "";
                foreach (object a in accs)
                {
                    var ad = AsDict(a);
                    granted += ToDouble(ad, "totalGranted");
                    if (who.Length == 0)
                    {
                        who = Str(ad, "label");
                        if (who.Length == 0) who = Str(ad, "email");
                    }
                }
                if (accs.Length > 1) who = accs.Length + " 个账号";

                bool ok = r.ContainsKey("ok") && Convert.ToBoolean(r["ok"]);
                string state;
                if (ok) state = granted > 0 ? "已领取" : "无新增";
                else
                {
                    state = Str(r, "reason");
                    if (state.Length == 0) state = "失败";
                }

                var it = new ListViewItem(ShortTime(Str(r, "at")));
                it.SubItems.Add(who);
                it.SubItems.Add(granted > 0 ? granted.ToString("0") : "0");
                it.SubItems.Add(state);
                it.ForeColor = !ok ? Bad : (granted > 0 ? Ok : Ink2);
                _runsList.Items.Add(it);
            }
            _runsList.EndUpdate();
        }

        // ---------------------------------------------------------------- 操作

        private void RunCheckin(string accountId)
        {
            if (_busy) return;
            _busy = true;
            _btnRun.Enabled = false;
            _btnRunOne.Enabled = false;
            string oldText = _btnRun.Text;
            _btnRun.Text = "执行中…";

            try
            {
                var payload = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(accountId)) payload["account"] = accountId;
                ApiPost("/api/run/async", _json.Serialize(payload));
            }
            catch (Exception ex)
            {
                EndRun(oldText);
                MessageBox.Show("触发失败：" + ex.Message, "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var waiter = new System.Windows.Forms.Timer { Interval = 2000 };
            int ticks = 0;
            waiter.Tick += (s, e) =>
            {
                ticks++;
                RefreshLog();
                RefreshStatus();
                bool stillRunning = false;
                try
                {
                    var st = AsDict(_json.DeserializeObject(ApiGet("/api/status")));
                    stillRunning = st.ContainsKey("running") && Convert.ToBoolean(st["running"]);
                }
                catch { }
                if (ticks >= 40 || (!stillRunning && ticks >= 3))
                {
                    waiter.Stop();
                    waiter.Dispose();
                    EndRun(oldText);
                }
            };
            waiter.Start();
        }

        private void EndRun(string label)
        {
            _busy = false;
            _btnRun.Enabled = true;
            _btnRunOne.Enabled = true;
            _btnRun.Text = string.IsNullOrEmpty(label) ? "全部执行" : label;
        }

        private void ToggleAccount()
        {
            if (string.IsNullOrEmpty(_selectedId))
            {
                MessageBox.Show("请先在列表里点选一个账号。", "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                AccountRow row = null;
                foreach (var r in _rowViews) if (r.AccountId == _selectedId) row = r;
                bool disable = row == null || row.Extra != "已停用";
                var body = new Dictionary<string, object> { { "enabled", !disable } };
                ApiPatch("/api/accounts/" + Uri.EscapeDataString(_selectedId), _json.Serialize(body));
                RefreshStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败：" + ex.Message, "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteAccount()
        {
            if (string.IsNullOrEmpty(_selectedId))
            {
                MessageBox.Show("请先在列表里点选一个账号。", "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("确定删除选中的账号吗？删除后需要重新登录才能再加回来。", "ANS 每日签到",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                ApiDelete("/api/accounts/" + Uri.EscapeDataString(_selectedId));
                _selectedId = null;
                RefreshStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败：" + ex.Message, "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AddAccount()
        {
            using (var dlg = new AddAccountForm((path, body) => ApiPost(path, body), _s))
            {
                dlg.ShowDialog(this);
                if (dlg.Saved)
                {
                    RefreshStatus();
                }
            }
        }

        private void SaveQuests()
        {
            var quests = new List<string>();
            if (_cbCheckin.Checked) quests.Add("daily_checkin");
            if (_cbRead.Checked) quests.Add("daily_vibehub_read");
            if (_cbStar.Checked) quests.Add("daily_explore_star");
            if (_cbFeed.Checked) quests.Add("daily_agent_feed");
            if (quests.Count == 0)
            {
                MessageBox.Show("至少要保留一个任务。", "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                ApiPost("/api/config", _json.Serialize(new Dictionary<string, object> { { "quests", quests } }));
                FlashSaved(_btnSaveQuests);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveTimes()
        {
            var list = new List<string>();
            foreach (string part in (_times.Text ?? "").Split(','))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                DateTime parsed;
                if (!DateTime.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    MessageBox.Show("时间格式不对：" + t + "\n应为 HH:mm，例如 12:00", "ANS 每日签到",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                list.Add(parsed.ToString("HH:mm"));
            }
            if (list.Count == 0)
            {
                MessageBox.Show("至少填一个时间。", "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var r = AsDict(_json.DeserializeObject(ApiPost("/api/schedule",
                    _json.Serialize(new Dictionary<string, object> { { "times", list } }))));
                FlashSaved(_btnSaveTimes);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存时间失败：" + ex.Message, "ANS 每日签到", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>保存后按钮短暂显示"已保存"，1.2 秒后恢复</summary>
        private void FlashSaved(RoundButton btn)
        {
            if (btn == null) return;
            string original = btn.Text;
            btn.Text = "已保存";
            btn.Enabled = false;
            var timer = new System.Windows.Forms.Timer { Interval = 1200 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                btn.Text = original;
                btn.Enabled = true;
            };
            timer.Start();
        }

        /// <summary>关闭窗口只收进托盘，不退出程序</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------ 自绘控件

    /// <summary>圆角白底卡片</summary>
    internal class CardPanel : Panel
    {
        public int Radius = 14;
        public Color BorderColor = Color.FromArgb(228, 231, 240);

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        private float DpiScale
        {
            get
            {
                float dpi = DeviceDpi;
                return dpi <= 0 ? 1F : dpi / 96F;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = Round(new Rectangle(0, 0, Width, Height), (int)Math.Round(Radius * DpiScale)))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(Radius * DpiScale)))
            using (var pen = new Pen(BorderColor))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        internal static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            // 控件刚创建/布局时可能是 0 尺寸，AddArc 会抛"参数无效"，这里直接退化成矩形
            if (r.Width <= 1 || r.Height <= 1)
            {
                path.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
                return path;
            }
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>胶囊标签（账号正常/需登录那类状态）</summary>
    internal class PillLabel : Label
    {
        public PillLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.Transparent);
            int radius = Math.Max(1, Height / 2);
            using (var path = CardPanel.Round(new Rectangle(0, 0, Width - 1, Height - 1), radius))
            using (var b = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(b, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>圆角按钮</summary>
    internal class RoundButton : Button
    {
        public bool Primary;
        public bool Danger;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Height = 34;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            Cursor = Cursors.Hand;
            BackColor = Color.White;
            ForeColor = Color.FromArgb(16, 21, 41);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position)) && Enabled;

            Color fill = Color.White;
            Color border = Color.FromArgb(216, 219, 231);
            Color text = Color.FromArgb(16, 21, 41);

            if (Primary) { fill = hover ? Color.FromArgb(79, 79, 226) : Color.FromArgb(91, 91, 246); border = fill; text = Color.White; }
            else if (Danger) { text = Color.FromArgb(220, 38, 38); fill = hover ? Color.FromArgb(254, 242, 242) : Color.White; }
            else if (hover) { fill = Color.FromArgb(246, 247, 251); }
            if (!Enabled) { fill = Color.FromArgb(240, 241, 246); text = Color.FromArgb(152, 162, 179); }

            float dpi = DeviceDpi <= 0 ? 96F : DeviceDpi;
            int radius = (int)Math.Round(Height / 2F);
            using (var path = CardPanel.Round(new Rectangle(0, 0, Width - 1, Height - 1), radius))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border))
            {
                e.Graphics.FillPath(b, path);
                e.Graphics.DrawPath(p, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>积分历史折线（自绘，含渐变面积）</summary>
    internal class SparkChart : Control
    {
        public double[] Series = new double[0];

        public SparkChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float dpi = DeviceDpi <= 0 ? 96F : DeviceDpi;
            float s = dpi / 96F;
            Func<int, int> px = v => (int)Math.Round(v * s);

            using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, ClientRectangle);

            if (Series == null || Series.Length < 2)
            {
                TextRenderer.DrawText(g, "该账号记录还不足 2 次", Font, ClientRectangle,
                    Color.FromArgb(152, 162, 179),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double min = Series[0], max = Series[0];
            foreach (double v in Series)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            bool flat = Math.Abs(max - min) < 0.0001;
            double span = flat ? 1 : max - min;

            int padX = px(8);
            int padTop = px(16);
            int padBottom = px(20);
            int W = Math.Max(2, Width - padX * 2);
            int H = Math.Max(2, Height - padTop - padBottom);

            using (var pen = new Pen(Color.FromArgb(240, 242, 247)))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int gy = padTop + H * i / 4;
                    g.DrawLine(pen, padX, gy, padX + W, gy);
                }
            }

            var pts = new PointF[Series.Length];
            for (int i = 0; i < Series.Length; i++)
            {
                float x = padX + (float)(i * (double)W / (Series.Length - 1));
                float y = flat
                    ? padTop + H / 2F
                    : (float)(padTop + H - ((Series[i] - min) / span) * H);
                pts[i] = new PointF(x, y);
            }

            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                PointF last = pts[pts.Length - 1];
                path.AddLine(last.X, last.Y, last.X, padTop + H);
                path.AddLine(last.X, padTop + H, pts[0].X, padTop + H);
                path.CloseFigure();
                using (var brush = new LinearGradientBrush(
                    new Rectangle(padX, padTop, W, H),
                    Color.FromArgb(46, 91, 91, 246),
                    Color.FromArgb(0, 91, 91, 246),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
            }

            using (var pen = new Pen(Color.FromArgb(91, 91, 246), Math.Max(1.4F, s * 1.4F)))
            {
                g.DrawLines(pen, pts);
            }

            using (var dot = new SolidBrush(Color.FromArgb(91, 91, 246)))
            {
                int d = px(6);
                foreach (PointF p in pts) g.FillEllipse(dot, p.X - d / 2F, p.Y - d / 2F, d, d);
            }

            using (var f = new Font("Microsoft YaHei UI", 8F))
            {
                TextRenderer.DrawText(g, max.ToString("0.##"), f, new Point(padX, px(2)),
                    Color.FromArgb(152, 162, 179), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, min.ToString("0.##"), f,
                    new Rectangle(padX, Height - padBottom, W, padBottom),
                    Color.FromArgb(152, 162, 179), TextFormatFlags.NoPadding | TextFormatFlags.Top);
            }
        }
    }

    /// <summary>账号行（自绘，保证在高 DPI 下依然清晰）</summary>
    internal class AccountRow : Control
    {
        public string AccountId = "";
        public string Email = "";
        public string Nickname = "";
        public string Points = "—";
        public string StatusKind = "";
        public string StatusText = "";
        public string LastText = "—";
        public string Extra = "";
        public bool Selected;

        private bool _hover;

        public AccountRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float dpi = DeviceDpi <= 0 ? 96F : DeviceDpi;
            float s = dpi / 96F;
            Func<int, int> px = v => (int)Math.Round(v * s);

            var full = new Rectangle(0, 0, Width, Height);
            Color back = Selected ? Color.FromArgb(243, 243, 255) : (_hover ? Color.FromArgb(249, 250, 253) : Color.White);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, full);
            using (var pen = new Pen(Color.FromArgb(240, 242, 247)))
            {
                g.DrawLine(pen, px(16), Height - 1, Width - px(16), Height - 1);
            }

            if (Selected)
            {
                using (var b = new SolidBrush(Color.FromArgb(91, 91, 246)))
                {
                    g.FillRectangle(b, new Rectangle(0, 0, px(3), Height));
                }
            }

            // 头像
            int avatarSize = px(38);
            var avatarRect = new Rectangle(px(16), (Height - avatarSize) / 2, avatarSize, avatarSize);
            using (var path = CardPanel.Round(avatarRect, px(10)))
            using (var b = new SolidBrush(Color.FromArgb(238, 238, 255)))
            {
                g.FillPath(b, path);
            }
            string initial = string.IsNullOrEmpty(Nickname)
                ? (Email.Length > 0 ? Email.Substring(0, 1) : "?")
                : Nickname.Substring(0, 1);
            using (var f = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, initial, f, avatarRect, Color.FromArgb(91, 91, 246),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int textLeft = px(66);
            using (var f = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, Email, f, new Point(textLeft, px(10)), Color.FromArgb(16, 21, 41),
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            string sub = Nickname;
            if (!string.IsNullOrEmpty(Extra)) sub = string.IsNullOrEmpty(sub) ? Extra : sub + " · " + Extra;
            using (var f = new Font("Microsoft YaHei UI", 8.5F))
            {
                TextRenderer.DrawText(g, sub, f, new Point(textLeft, px(34)), Color.FromArgb(152, 162, 179),
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            int right = Width - px(16);
            using (var f = new Font("Microsoft YaHei UI", 8F))
            {
                TextRenderer.DrawText(g, LastText, f, new Rectangle(right - px(120), 0, px(120), Height),
                    Color.FromArgb(152, 162, 179),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            string status = "● " + (string.IsNullOrEmpty(StatusText) ? "—" : StatusText);
            using (var f = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, status, f, new Rectangle(right - px(260), 0, px(130), Height),
                    StatusColor(StatusKind),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            using (var f = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, Points, f, new Rectangle(right - px(380), 0, px(100), Height),
                    Color.FromArgb(16, 21, 41),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private static Color StatusColor(string kind)
        {
            switch (kind)
            {
                case "granted": return Color.FromArgb(5, 150, 105);
                case "duplicate": return Color.FromArgb(5, 150, 105);
                case "rejected": return Color.FromArgb(180, 83, 9);
                case "error": return Color.FromArgb(220, 38, 38);
                default: return Color.FromArgb(152, 162, 179);
            }
        }
    }
}
