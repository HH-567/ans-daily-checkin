using System;
using System.Drawing;
using System.Windows.Forms;

namespace AnsCheckin
{
    /// <summary>
    /// 添加账号对话框：提供网页版同样的三种方式。
    /// 只负责收集输入并调用本地接口，具体登录逻辑仍在 checkin/ans-auth 里。
    /// </summary>
    internal sealed class AddAccountForm : Form
    {
        private readonly Func<string, string, string> _post; // (path, jsonBody) -> response
        private readonly float _s;

        private RadioButton _rbPassword;
        private RadioButton _rbCookie;
        private RadioButton _rbBrowser;
        private Panel _pagePassword;
        private Panel _pageCookie;
        private Panel _pageBrowser;

        private TextBox _email;
        private TextBox _password;
        private TextBox _cookie;
        private Label _hint;
        private Button _btnPrimary;

        public bool Saved { get; private set; }

        private int Px(int v)
        {
            return (int)Math.Round(v * _s);
        }

        public AddAccountForm(Func<string, string, string> post, float scale)
        {
            _post = post;
            _s = scale < 1F ? 1F : scale;

            Text = "添加账号";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(Px(520), Px(420));
            BackColor = Color.FromArgb(242, 243, 246);
            Font = new Font("Microsoft YaHei UI", 9.5F);
            AutoScaleMode = AutoScaleMode.None;

            BuildUi();
            SelectMode(0);
        }

        private void BuildUi()
        {
            var header = new Label
            {
                Text = "添加账号",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(16, 21, 41),
                AutoSize = true,
                Left = Px(24),
                Top = Px(18),
            };
            Controls.Add(header);

            Controls.Add(new Label
            {
                Text = "选择一种方式，登录成功后账号会自动保存到本机。",
                Font = new Font("Microsoft YaHei UI", 8.5F),
                ForeColor = Color.FromArgb(152, 162, 179),
                AutoSize = true,
                Left = Px(26),
                Top = Px(46),
            });

            // 方式选择
            _rbPassword = MakeRadio("账号密码", Px(24), Px(80), 0);
            _rbCookie = MakeRadio("粘贴 Cookie", Px(160), Px(80), 1);
            _rbBrowser = MakeRadio("浏览器登录", Px(310), Px(80), 2);

            var card = new CardPanel
            {
                Left = Px(24),
                Top = Px(116),
                Width = ClientSize.Width - Px(48),
                Height = Px(196),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(card);

            // 页：账号密码
            _pagePassword = NewPage();
            _pagePassword.Controls.Add(LabelAt("邮箱", Px(20), Px(18)));
            _email = TextBoxAt(_pagePassword, Px(20), Px(40), Px(300));
            _pagePassword.Controls.Add(LabelAt("密码", Px(20), Px(78)));
            _password = TextBoxAt(_pagePassword, Px(20), Px(100), Px(300));
            _password.UseSystemPasswordChar = true;
            _pagePassword.Controls.Add(new Label
            {
                Text = "密码只在内存中用于换取登录凭证，不会保存到磁盘。",
                Font = new Font("Microsoft YaHei UI", 8F),
                ForeColor = Color.FromArgb(152, 162, 179),
                AutoSize = true,
                Left = Px(20),
                Top = Px(140),
            });
            card.Controls.Add(_pagePassword);

            // 页：Cookie
            _pageCookie = NewPage();
            _pageCookie.Controls.Add(LabelAt("粘贴 Cookie（也支持直接从浏览器复制的 cURL）", Px(20), Px(18)));
            _cookie = new TextBox
            {
                Left = Px(20),
                Top = Px(42),
                Width = card.Width - Px(40),
                Height = Px(96),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _pageCookie.Controls.Add(_cookie);
            _pageCookie.Controls.Add(new Label
            {
                Text = "从 ans.cauai.fun 登录后按 F12 → Network → 复制请求头里的 Cookie。",
                Font = new Font("Microsoft YaHei UI", 8F),
                ForeColor = Color.FromArgb(152, 162, 179),
                AutoSize = true,
                Left = Px(20),
                Top = Px(146),
            });
            card.Controls.Add(_pageCookie);

            // 页：浏览器登录
            _pageBrowser = NewPage();
            _pageBrowser.Controls.Add(new Label
            {
                Text = "打开一个独立的浏览器窗口，你手动完成登录。\n检测到登录成功会自动保存凭证并关闭窗口。",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(90, 99, 122),
                AutoSize = true,
                Left = Px(20),
                Top = Px(30),
            });
            _pageBrowser.Controls.Add(new Label
            {
                Text = "这是唯一会用到浏览器的方式，不会影响你日常使用的浏览器。",
                Font = new Font("Microsoft YaHei UI", 8F),
                ForeColor = Color.FromArgb(152, 162, 179),
                AutoSize = true,
                Left = Px(20),
                Top = Px(96),
            });
            card.Controls.Add(_pageBrowser);

            _hint = new Label
            {
                AutoSize = true,
                Left = Px(26),
                Top = Px(324),
                ForeColor = Color.FromArgb(220, 38, 38),
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Text = "",
            };
            Controls.Add(_hint);

            _btnPrimary = new Button
            {
                Text = "登录",
                Left = ClientSize.Width - Px(120) - Px(24),
                Top = Px(348),
                Width = Px(120),
                Height = Px(36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(91, 91, 246),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _btnPrimary.FlatAppearance.BorderSize = 0;
            _btnPrimary.Click += (s, e) => Submit();
            Controls.Add(_btnPrimary);

            var btnCancel = new Button
            {
                Text = "取消",
                Left = ClientSize.Width - Px(120) - Px(24) - Px(100),
                Top = Px(348),
                Width = Px(88),
                Height = Px(36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(16, 21, 41),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(216, 219, 231);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);

            AcceptButton = _btnPrimary;
            CancelButton = btnCancel;
        }

        private Panel NewPage()
        {
            return new Panel
            {
                Left = 0,
                Top = 0,
                Width = Px(464),
                Height = Px(196),
                BackColor = Color.White,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
        }

        private Label LabelAt(string text, int left, int top)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Left = left,
                Top = top,
                ForeColor = Color.FromArgb(90, 99, 122),
                Font = new Font("Microsoft YaHei UI", 8.5F),
            };
        }

        private TextBox TextBoxAt(Control parent, int left, int top, int width)
        {
            var tb = new TextBox
            {
                Left = left,
                Top = top,
                Width = width,
                Font = new Font("Microsoft YaHei UI", 10F),
                BorderStyle = BorderStyle.FixedSingle,
            };
            parent.Controls.Add(tb);
            return tb;
        }

        private RadioButton MakeRadio(string text, int left, int top, int index)
        {
            var rb = new RadioButton
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                ForeColor = Color.FromArgb(16, 21, 41),
                Tag = index,
            };
            rb.CheckedChanged += (s, e) =>
            {
                var r = (RadioButton)s;
                if (r.Checked) SelectMode((int)r.Tag);
            };
            Controls.Add(rb);
            return rb;
        }

        private void SelectMode(int index)
        {
            _pagePassword.Visible = index == 0;
            _pageCookie.Visible = index == 1;
            _pageBrowser.Visible = index == 2;
            _hint.Text = "";
            _btnPrimary.Text = index == 2 ? "打开登录窗口" : "登录";
        }

        private void Submit()
        {
            _hint.Text = "";
            _btnPrimary.Enabled = false;
            try
            {
                if (_pagePassword.Visible)
                {
                    string email = (_email.Text ?? "").Trim();
                    string pwd = _password.Text ?? "";
                    if (email.Length == 0 || pwd.Length == 0)
                    {
                        _hint.Text = "请填写邮箱和密码";
                        return;
                    }
                    var body = "{\"email\":" + Json(email) + ",\"password\":" + Json(pwd) + "}";
                    _post("/api/login/password", body);
                    _password.Text = "";
                    Saved = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else if (_pageCookie.Visible)
                {
                    string cookie = (_cookie.Text ?? "").Trim();
                    if (cookie.Length == 0)
                    {
                        _hint.Text = "请粘贴 Cookie";
                        return;
                    }
                    var body = "{\"cookie\":" + Json(cookie) + "}";
                    _post("/api/import/cookie", body);
                    _cookie.Text = "";
                    Saved = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    // 浏览器登录要等人操作，HTTP 请求会挂住，放到后台线程里发
                    var post = _post;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { post("/api/login", "{}"); } catch { }
                    });
                    Saved = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                _hint.Text = ex.Message;
            }
            finally
            {
                _btnPrimary.Enabled = true;
            }
        }

        private static string Json(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
