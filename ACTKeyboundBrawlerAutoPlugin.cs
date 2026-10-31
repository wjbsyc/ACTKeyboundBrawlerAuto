using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;

[assembly: AssemblyTitle("ACTKeyboundBrawlerAuto")]
[assembly: AssemblyDescription("FFXIV 国服魔光键影词表收集与可选自动输入插件")]
[assembly: AssemblyVersion("0.4.5.1")]

namespace ACTKeyboundBrawlerAuto
{
    public class ACTKeyboundBrawlerAutoPlugin : UserControl, IActPluginV1
    {
        private CheckBox _chkEnabled;
        private CheckBox _chkWriteLog;
        private CheckBox _chkRawHex;
        private CheckBox _chkAllPlayers;
        private CheckBox _chkAutoType;
        private NumericUpDown _nudMinDelay;
        private NumericUpDown _nudMaxDelay;
        private Label _lblWordStatus;
        private Label _lblUnknownPath;
        private Label _lblLogPath;
        private Label _lblProtocol;
        private Label _lblMinDelay;
        private Label _lblMaxDelay;
        private Label _lblAutoStatus;
        private Label _lblAutoHint;
        private Button _btnReloadWords;
        private Button _btnSaveUnknown;
        private Button _btnOpenWords;
        private Button _btnOpenLogs;
        private Button _btnTestInput;
        private Button _btnClear;
        private GroupBox _grpSettings;
        private GroupBox _grpOutput;
        private RichTextBox _txtOutput;

        private readonly BrawlerParser _parser = new BrawlerParser();
        private readonly WordTable _wordTable = new WordTable();
        private BrawlerLogger _logger = new BrawlerLogger();
        private System.Windows.Forms.Timer _hookTimer;
        private UserKeyboardMonitor _userKeyboardMonitor;
        private System.Windows.Forms.Timer _testInputTimer;
        private int _testInputCountdown;

        private Label _statusLabel;
        private SettingsSerializer _settings;
        private string _settingsFile;
        private string _pluginDirectory;
        private string _wordsFile;
        private string _unknownFile;
        private string _logDirectory;
        private bool _initialized;
        private bool _hookWarningShown;
        private int _timerTicks;
        private int _outputLineCount;
        private const int MaxOutputLines = 1200;

        private readonly object _autoLock = new object();
        private readonly Dictionary<uint, AutoWordTarget> _autoWords =
            new Dictionary<uint, AutoWordTarget>();
        private readonly HashSet<string> _autoSuppressedTexts =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<ulong> _autoSuppressedKeys = new HashSet<ulong>();
        private readonly Random _autoRandom = new Random();
        private System.Threading.Timer _autoTypeTimer;
        private DateTime _lastAutoAssignTime = DateTime.MinValue;
        private long _autoWordOrder;
        private long _autoRoundToken;
        private uint _autoTypingCarrier;
        private bool _autoWaitingForSubmit;
        private bool _autoInputBusy;
        private bool _autoNeedsClear;
        private AutoWordTarget _autoPendingSubmit;
        private readonly TypingPauseController _typingPause = new TypingPauseController();
        private AutoWordTarget _lastAutoSubmittedTarget;
        private bool _autoRoundActive;
        private decimal _autoMinDelaySeconds = 0.0m;
        private decimal _autoMaxDelaySeconds = 0.5m;
        private bool _settingsLoading;
        internal Func<string, int, UnicodeInputSender.Result> TypeInput = UnicodeInputSender.TypeText;
        internal Func<UnicodeInputSender.Result> SubmitInput = UnicodeInputSender.Submit;

        private object _ffxivDataSubscription;
        private Delegate _networkReceivedDelegate;
        private EventInfo _networkReceivedEvent;
        private bool _ffxivHooked;

        private sealed class AutoWordTarget
        {
            public uint CarrierId { get; set; }
            public uint TableTag { get; set; }
            public uint WordIndex { get; set; }
            public string Text { get; set; }
            public long QueueOrder { get; set; }
            public long Revision { get; set; }
            public long AssignmentRevision { get; set; }
            public long RoundToken { get; set; }
            public DateTime AssignedAt { get; set; }
            public int AttemptCount { get; set; }
            public bool HasSubmitted { get; set; }
            public bool IsActive { get; set; }
        }

        public ACTKeyboundBrawlerAutoPlugin()
        {
            InitializeUI();
            TypeInput = delegate(string text, int delay)
            {
                return UnicodeInputSender.TypeText(text, delay, CanSendAutoInput);
            };
            SubmitInput = delegate { return UnicodeInputSender.Submit(CanSendAutoInput); };
        }

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            if (_initialized) return;

            _statusLabel = pluginStatusText;
            _pluginDirectory = GetPluginDirectory();
            _wordsFile = Path.Combine(_pluginDirectory, "words.json");
            _unknownFile = Path.Combine(_pluginDirectory, "unknown_words.json");
            _logDirectory = Path.Combine(_pluginDirectory, "logs");
            _settingsFile = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config", "ACTKeyboundBrawlerAuto.config.xml");

            pluginScreenSpace.Controls.Add(this);
            pluginScreenSpace.Text = "魔光键影";
            Dock = DockStyle.Fill;

            _wordTable.Load(_wordsFile, _unknownFile);
            _hookTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _hookTimer.Tick += OnHookTimerTick;

            _settings = new SettingsSerializer(this);
            _settingsLoading = true;
            LoadSettings();
            _settingsLoading = false;
            UpdateDelaySettingsFromControls();
            _logger.WriteLine("INFO", string.Format(
                "自动输入延迟设置: {0:F1}s~{1:F1}s",
                _autoMinDelaySeconds, _autoMaxDelaySeconds));
            ActGlobals.oFormActMain.BeforeLogLineRead += OnBeforeLogLineRead;

            try
            {
                _userKeyboardMonitor = new UserKeyboardMonitor();
            }
            catch (Exception ex)
            {
                _logger.WriteLine("WARN", "用户键盘监听创建失败: " + ex.Message);
            }

            _initialized = true;
            UpdateWordStatus();
            UpdateFileLabels();

            if (_chkEnabled.Checked)
                OnEnabled();
            if (AutoTypeEnabled)
                StartAutoTyping();

            _statusLabel.Text = "ACTKeyboundBrawlerAuto 已加载";
            AppendOutput("[系统] ACTKeyboundBrawlerAuto 已启动", Color.Gray);
            AppendOutput("[系统] 插件目录: " + _pluginDirectory, Color.Gray);
            AppendOutput("[系统] " + _wordTable.GetStatus(), Color.Gray);
            foreach (string warning in _wordTable.Warnings)
                AppendOutput("[词表] " + warning, Color.Orange);
        }

        public void DeInitPlugin()
        {
            if (!_initialized) return;

            ActGlobals.oFormActMain.BeforeLogLineRead -= OnBeforeLogLineRead;
            OnDisabled();
            if (_hookTimer != null)
            {
                _hookTimer.Stop();
                _hookTimer.Dispose();
                _hookTimer = null;
            }

            if (_testInputTimer != null)
            {
                _testInputTimer.Stop();
                _testInputTimer.Dispose();
                _testInputTimer = null;
                if (_btnTestInput != null)
                {
                    _btnTestInput.Enabled = true;
                    _btnTestInput.Text = "测试输入";
                }
            }

            SaveSettings();
            _initialized = false;
            if (_userKeyboardMonitor != null)
            {
                _userKeyboardMonitor.Dispose();
                _userKeyboardMonitor = null;
            }
            if (_statusLabel != null)
                _statusLabel.Text = "ACTKeyboundBrawlerAuto 已卸载";
        }

        #region UI

        private void InitializeUI()
        {
            SuspendLayout();

            _grpSettings = new GroupBox
            {
                Text = "设置",
                Dock = DockStyle.Top,
                Height = 204,
                Padding = new Padding(10, 6, 10, 6)
            };

            _chkEnabled = new CheckBox
            {
                Name = "chkEnabled",
                Text = "启用监听",
                Checked = true,
                AutoSize = true,
                Location = new Point(16, 28)
            };
            _chkEnabled.CheckedChanged += ChkEnabled_CheckedChanged;

            _chkWriteLog = new CheckBox
            {
                Name = "chkWriteLog",
                Text = "写独立日志",
                Checked = true,
                AutoSize = true,
                Location = new Point(125, 28)
            };
            _chkWriteLog.CheckedChanged += ChkWriteLog_CheckedChanged;

            _chkRawHex = new CheckBox
            {
                Name = "chkRawHex",
                Text = "记录原始HEX",
                Checked = false,
                AutoSize = true,
                Location = new Point(238, 28)
            };

            _chkAllPlayers = new CheckBox
            {
                Name = "chkAllPlayers",
                Text = "记录所有玩家回执",
                Checked = false,
                AutoSize = true,
                Location = new Point(352, 28)
            };

            _chkAutoType = new CheckBox
            {
                Name = "chkAutoType",
                Text = "启用自动输入",
                Checked = false,
                AutoSize = true,
                Location = new Point(16, 136)
            };
            _chkAutoType.CheckedChanged += ChkAutoType_CheckedChanged;

            _lblMinDelay = new Label
            {
                Text = "最小延迟(秒)",
                AutoSize = true,
                Location = new Point(115, 140)
            };

            _nudMinDelay = new NumericUpDown
            {
                Name = "nudMinDelay",
                Minimum = 0m,
                Maximum = 30m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 0.0m,
                Width = 64,
                Location = new Point(202, 136)
            };
            _nudMinDelay.ValueChanged += DelaySetting_ValueChanged;

            _lblMaxDelay = new Label
            {
                Text = "最大延迟(秒)",
                AutoSize = true,
                Location = new Point(280, 140)
            };

            _nudMaxDelay = new NumericUpDown
            {
                Name = "nudMaxDelay",
                Minimum = 0m,
                Maximum = 30m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 0.5m,
                Width = 64,
                Location = new Point(367, 136)
            };
            _nudMaxDelay.ValueChanged += DelaySetting_ValueChanged;

            _lblAutoStatus = new Label
            {
                Text = "自动输入: 未启用",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(16, 164)
            };

            _btnTestInput = new Button
            {
                Name = "btnTestInput",
                Text = "测试输入",
                Bounds = new Rectangle(450, 132, 96, 26)
            };
            _btnTestInput.Click += BtnTestInput_Click;

            _lblAutoHint = new Label
            {
                Text = "仅前台FFXIV有效；输入后等待随机延迟，到期直接回车提交",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(16, 184)
            };

            _btnReloadWords = new Button
            {
                Name = "btnReloadWords",
                Text = "重载词表",
                Bounds = new Rectangle(16, 60, 88, 26)
            };
            _btnReloadWords.Click += BtnReloadWords_Click;

            _btnSaveUnknown = new Button
            {
                Name = "btnSaveUnknown",
                Text = "保存未知表",
                Bounds = new Rectangle(110, 60, 96, 26)
            };
            _btnSaveUnknown.Click += BtnSaveUnknown_Click;

            _btnOpenWords = new Button
            {
                Name = "btnOpenWords",
                Text = "打开词表",
                Bounds = new Rectangle(214, 60, 96, 26)
            };
            _btnOpenWords.Click += BtnOpenWords_Click;

            _btnOpenLogs = new Button
            {
                Name = "btnOpenLogs",
                Text = "日志目录",
                Bounds = new Rectangle(318, 60, 88, 26)
            };
            _btnOpenLogs.Click += BtnOpenLogs_Click;

            _lblProtocol = new Label
            {
                Text = "下发: 01E6/10000008 → P5=词表, P6=当前词, P7=下一词；回执: 00BD/C16C-C170",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(16, 94)
            };

            _lblWordStatus = new Label
            {
                Text = "词表: 未加载",
                AutoSize = true,
                ForeColor = Color.DarkCyan,
                Location = new Point(16, 114)
            };

            _lblLogPath = new Label
            {
                Text = "独立日志: 未启用",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(300, 114)
            };

            _grpSettings.Controls.AddRange(new Control[]
            {
                _chkEnabled, _chkWriteLog, _chkRawHex, _chkAllPlayers,
                _btnReloadWords, _btnSaveUnknown, _btnOpenWords, _btnOpenLogs,
                _chkAutoType, _lblMinDelay, _nudMinDelay, _lblMaxDelay, _nudMaxDelay,
                _btnTestInput,
                _lblProtocol, _lblWordStatus, _lblLogPath, _lblAutoStatus, _lblAutoHint
            });

            var toolPanel = new Panel { Dock = DockStyle.Top, Height = 34 };

            _btnClear = new Button
            {
                Text = "清空输出",
                Bounds = new Rectangle(8, 4, 88, 24)
            };
            _btnClear.Click += delegate { _txtOutput.Clear(); _outputLineCount = 0; };

            _lblUnknownPath = new Label
            {
                Text = "未知词: unknown_words.json",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(108, 9)
            };
            toolPanel.Controls.Add(_btnClear);
            toolPanel.Controls.Add(_lblUnknownPath);

            _grpOutput = new GroupBox
            {
                Text = "解析输出",
                Dock = DockStyle.Fill,
                Padding = new Padding(6)
            };

            _txtOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                DetectUrls = false,
                BackColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None
            };
            _grpOutput.Controls.Add(_txtOutput);

            Controls.Add(_grpOutput);
            Controls.Add(toolPanel);
            Controls.Add(_grpSettings);
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hookTimer != null)
            {
                _hookTimer.Stop();
                _hookTimer.Dispose();
                _hookTimer = null;
            }

            if (disposing && _testInputTimer != null)
            {
                _testInputTimer.Stop();
                _testInputTimer.Dispose();
                _testInputTimer = null;
            }

            if (disposing && _userKeyboardMonitor != null)
            {
                _userKeyboardMonitor.Dispose();
                _userKeyboardMonitor = null;
            }

            if (disposing)
                DisposeAutoTimer();

            base.Dispose(disposing);
        }

        private void AppendOutput(string line, Color color)
        {
            if (_txtOutput == null || !_txtOutput.IsHandleCreated) return;
            if (_txtOutput.InvokeRequired)
            {
                try
                {
                    _txtOutput.BeginInvoke(new Action(() => AppendOutput(line, color)));
                }
                catch { }
                return;
            }

            _txtOutput.SelectionStart = _txtOutput.TextLength;
            _txtOutput.SelectionColor = color;
            _txtOutput.AppendText(line + Environment.NewLine);
            _txtOutput.SelectionColor = _txtOutput.ForeColor;
            _outputLineCount++;

            if (_outputLineCount > MaxOutputLines)
            {
                int remove = _outputLineCount - MaxOutputLines;
                int firstLineEnd = _txtOutput.GetFirstCharIndexFromLine(remove);
                if (firstLineEnd > 0)
                {
                    int previousLength = _txtOutput.TextLength;
                    bool wasReadOnly = _txtOutput.ReadOnly;
                    try
                    {
                        // Empty SelectedText sends WM_CLEAR, which beeps and fails on a read-only RichEdit.
                        _txtOutput.ReadOnly = false;
                        _txtOutput.Select(0, firstLineEnd);
                        _txtOutput.SelectedText = String.Empty;
                        if (_txtOutput.TextLength == previousLength - firstLineEnd)
                            _outputLineCount = MaxOutputLines;
                    }
                    finally
                    {
                        _txtOutput.ReadOnly = wasReadOnly;
                    }
                }
            }

            _txtOutput.SelectionStart = _txtOutput.TextLength;
            _txtOutput.ScrollToCaret();
        }

        private void UpdateWordStatus()
        {
            if (_lblWordStatus == null) return;
            if (_lblWordStatus.InvokeRequired)
            {
                try { _lblWordStatus.BeginInvoke(new Action(UpdateWordStatus)); }
                catch { }
                return;
            }

            _lblWordStatus.Text = "状态: " + _wordTable.GetStatus();
        }

        private void UpdateFileLabels()
        {
            if (_lblLogPath != null)
            {
                _lblLogPath.Text = _logger != null && _logger.LogFilePath != null
                    ? "独立日志: " + _logger.LogFilePath
                    : "独立日志: 未启用";
            }

            if (_lblUnknownPath != null)
                _lblUnknownPath.Text = "未知词: " + (_unknownFile ?? "unknown_words.json");
        }

        #endregion

        #region FFXIV network hook

        private bool TryHookFfxivPlugin(bool quiet)
        {
            if (_ffxivHooked) return true;

            try
            {
                object ffxivPlugin = null;
                foreach (var plugin in ActGlobals.oFormActMain.ActPlugins)
                {
                    if (plugin.pluginFile == null || plugin.pluginObj == null) continue;
                    if (plugin.pluginFile.Name.Equals("FFXIV_ACT_Plugin.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        ffxivPlugin = plugin.pluginObj;
                        break;
                    }
                }

                if (ffxivPlugin == null)
                {
                    if (!quiet && !_hookWarningShown)
                    {
                        AppendOutput("[警告] 未找到 FFXIV_ACT_Plugin，稍后会自动重试", Color.Orange);
                        _hookWarningShown = true;
                    }
                    return false;
                }

                PropertyInfo subscriptionProperty = ffxivPlugin.GetType().GetProperty(
                    "DataSubscription", BindingFlags.Public | BindingFlags.Instance);
                if (subscriptionProperty == null)
                {
                    AppendOutput("[错误] FFXIV 插件没有 DataSubscription 接口", Color.Red);
                    return false;
                }

                _ffxivDataSubscription = subscriptionProperty.GetValue(ffxivPlugin, null);
                if (_ffxivDataSubscription == null)
                {
                    if (!quiet && !_hookWarningShown)
                    {
                        AppendOutput("[警告] FFXIV 插件 DataSubscription 尚未就绪", Color.Orange);
                        _hookWarningShown = true;
                    }
                    return false;
                }

                Type subscriptionType = _ffxivDataSubscription.GetType();
                _networkReceivedEvent = subscriptionType.GetEvent("NetworkReceived");
                if (_networkReceivedEvent == null)
                {
                    AppendOutput("[错误] FFXIV 插件没有 NetworkReceived 事件", Color.Red);
                    return false;
                }

                MethodInfo handler = GetType().GetMethod("OnNetworkReceived",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _networkReceivedDelegate = Delegate.CreateDelegate(
                    _networkReceivedEvent.EventHandlerType, this, handler);
                _networkReceivedEvent.AddEventHandler(_ffxivDataSubscription, _networkReceivedDelegate);

                _ffxivHooked = true;
                _hookWarningShown = false;
                AppendOutput("[系统] 已挂载 FFXIV 插件 NetworkReceived（服务器下发包）", Color.LimeGreen);
                return true;
            }
            catch (Exception ex)
            {
                AppendOutput("[错误] 挂载 NetworkReceived 失败: " + ex.Message, Color.Red);
                _logger.WriteLine("ERROR", "挂载 NetworkReceived 失败: " + ex);
                return false;
            }
        }

        private void UnhookFfxivPlugin()
        {
            try
            {
                if (_ffxivDataSubscription != null &&
                    _networkReceivedEvent != null &&
                    _networkReceivedDelegate != null)
                {
                    _networkReceivedEvent.RemoveEventHandler(_ffxivDataSubscription, _networkReceivedDelegate);
                }
            }
            catch { }

            _ffxivDataSubscription = null;
            _networkReceivedDelegate = null;
            _networkReceivedEvent = null;
            _ffxivHooked = false;
        }

        private void OnNetworkReceived(string connection, long epoch, byte[] message)
        {
            if (!_initialized || !_chkEnabled.Checked) return;

            try
            {
                BrawlerEvent evt = _parser.ParseBinaryPacket(message);
                if (evt == null) return;

                if (_chkRawHex.Checked)
                    evt.RawHex = "conn=" + connection + "|epoch=" + epoch + "|hex=" +
                                 BrawlerParser.FormatBinaryAsHex(message);

                HandleBrawlerEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.WriteLine("ERROR", "NetworkReceived 处理失败: " + ex);
            }
        }

        private void OnBeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            if (!_initialized || !_chkEnabled.Checked || isImport) return;

            string line = logInfo.logLine;
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                string pauseDetail;
                if (_typingPause.ProcessLogLine(line, _parser.LocalPlayerId, DateTime.UtcNow, out pauseDetail))
                {
                    _logger.WriteLine("AUTO", "TYPING_WINDOW|" + pauseDetail);
                    UpdateAutoStatus();
                }
                // Structured cast/status lines supplement binary word events.
                // Only raw 252 duplicates are excluded from the word parser.
                if (_ffxivHooked && !line.StartsWith("1|", StringComparison.Ordinal) &&
                    !line.StartsWith("01|", StringComparison.Ordinal)) return;

                BrawlerEvent evt = _parser.ParseLogLine(line);
                if (evt == null) return;

                // 二进制事件和 252 日志事件来自同一个包；二进制挂载成功时只走 NetworkReceived，避免重复。
                if (evt.EventType != BrawlerEventType.Zone && _ffxivHooked) return;

                if (evt.EventType != BrawlerEventType.Zone && _chkRawHex.Checked)
                    evt.RawHex = "text252|" + line.TrimEnd();

                HandleBrawlerEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.WriteLine("ERROR", "BeforeLogLineRead 处理失败: " + ex);
            }
        }

        #endregion

        #region Event handling

        private void HandleBrawlerEvent(BrawlerEvent evt)
        {
            if (evt == null) return;

            switch (evt.EventType)
            {
                case BrawlerEventType.Zone:
                    HandleZone(evt);
                    break;
                case BrawlerEventType.WordAssign:
                    HandleWordAssign(evt);
                    break;
                case BrawlerEventType.Typed:
                    HandleTyped(evt);
                    break;
                case BrawlerEventType.WordDisappear:
                    HandleWordDisappear(evt);
                    break;
                case BrawlerEventType.CarrierClear:
                    HandleCarrierClear(evt);
                    break;
                case BrawlerEventType.CastStart:
                    HandleCastStart(evt);
                    break;
            }
        }

        private void HandleZone(BrawlerEvent evt)
        {
            bool inside = evt.ZoneId == BrawlerOpcodes.GOLD_SAUCER_ZONE ||
                          evt.ZoneId == BrawlerOpcodes.KEYBOUND_BRAWLER_ZONE;
            string message = string.Format("ZONE|id=0x{0:X}|name={1}|goldSaucer={2}",
                evt.ZoneId, evt.ZoneName, inside ? "true" : "false");
            _logger.WriteLine("ZONE", message);
            AppendOutput("[区域] " + message, Color.Gray);
            if (!inside && AutoTypeEnabled)
                ResetAutoRound("left gold saucer");
            if (!inside)
                _typingPause.Reset();
        }

        private void HandleWordAssign(BrawlerEvent evt)
        {
            string currentText = _wordTable.GetText(evt.CurrentWordTableTag, evt.CurrentWordIndex);
            string nextText = _wordTable.GetText(evt.NextWordTableTag, evt.NextWordIndex);
            evt.WordText = currentText;
            evt.NextWordText = nextText;
            HandleAutoAssignment(evt, currentText);

            string context = string.Format(
                "ASSIGN table=0x{0:X8} phase={1:X} carrier=0x{2:X8} current=0x{3:X} next=0x{4:X}",
                evt.WordTableTag, evt.PhaseParam, evt.CarrierId, evt.CurrentWordIndex, evt.NextWordIndex);
            bool currentUnknown = _wordTable.RecordUnknown(
                evt.CurrentWordTableTag, evt.CurrentWordIndex, evt.Timestamp, context);
            bool nextUnknown = _wordTable.RecordUnknown(
                evt.NextWordTableTag, evt.NextWordIndex, evt.Timestamp, context);

            string message = string.Format(
                "ASSIGN|carrier=0x{0:X8}|content=0x{1:X8}|table=0x{2:X8}|phase={3:X}|current={4}|next={5}|src=0x{6:X8}|revision={7}",
                evt.CarrierId, evt.ContentId, evt.WordTableTag, evt.PhaseParam,
                _wordTable.Display(evt.CurrentWordTableTag, evt.CurrentWordIndex),
                _wordTable.Display(evt.NextWordTableTag, evt.NextWordIndex),
                evt.SourceActor, evt.AssignmentRevision);

            WriteEvent(evt, message);
            if (currentUnknown)
                WriteUnknown(evt.CurrentWordTableTag, evt.CurrentWordIndex, context);
            if (nextUnknown)
                WriteUnknown(evt.NextWordTableTag, evt.NextWordIndex, context);

            Color color = currentText == null || nextText == null ? Color.Orange : Color.LightGreen;
            AppendOutput("[下发] " + message, color);
            UpdateWordStatus();
        }

        private void HandleTyped(BrawlerEvent evt)
        {
            // Other players' results are diagnostic only; local prompts can differ.
            HandleAutoTypedResult(evt);

            if (!evt.IsLocalPlayer && !_chkAllPlayers.Checked) return;

            string text = _wordTable.GetText(evt.CurrentWordTableTag, evt.CurrentWordIndex);
            evt.WordText = text;
            string context = string.Format("TYPED action=0x{0:X} table=0x{1:X8} carrier=0x{2:X8} word=0x{3:X}",
                evt.ActionId, evt.CurrentWordTableTag, evt.CarrierId, evt.CurrentWordIndex);
            bool unknown = evt.HasCarrierState && _wordTable.RecordUnknown(
                evt.CurrentWordTableTag, evt.CurrentWordIndex, evt.Timestamp, context);

            string message = string.Format(
                "TYPED|by=0x{0:X8}|local={1}|carrier=0x{2:X8}|action={3}|table=0x{4:X8}|word={5}|handle=0x{6:X4}|counter=0x{7:X4}|seq=0x{8:X}|state={9}|revision={10}",
                evt.SourceActor, evt.IsLocalPlayer ? "true" : "false", evt.CarrierId,
                GetActionName(evt.ActionId), evt.CurrentWordTableTag,
                _wordTable.Display(evt.CurrentWordTableTag, evt.CurrentWordIndex),
                evt.WordHandle, evt.PlayerCounter, evt.RelatedSequence,
                evt.HasCarrierState ? "ok" : "missing", evt.AssignmentRevision);

            WriteEvent(evt, message);
            if (unknown)
                WriteUnknown(evt.CurrentWordTableTag, evt.CurrentWordIndex, context);

            Color color = !evt.HasCarrierState ? Color.OrangeRed :
                text == null ? Color.Orange : Color.FromArgb(140, 220, 255);
            AppendOutput("[回执] " + message, color);
            UpdateWordStatus();
        }

        private void HandleWordDisappear(BrawlerEvent evt)
        {
            string text = _wordTable.GetText(evt.CurrentWordTableTag, evt.CurrentWordIndex);
            evt.WordText = text;
            string message = string.Format(
                "DISAPPEAR|carrier=0x{0:X8}|derived={1}|command=0x{2:X8}|param=0x{3:X}|table=0x{4:X8}|word={5}|revision={6}",
                evt.CarrierId, evt.DerivedCarrierId, evt.CommandId, evt.CommandParam,
                evt.CurrentWordTableTag, _wordTable.Display(evt.CurrentWordTableTag, evt.CurrentWordIndex),
                evt.AssignmentRevision);

            WriteEvent(evt, message);
            AppendOutput("[消失] " + message,
                evt.CarrierId == 0 ? Color.Orange : Color.FromArgb(255, 190, 120));
            if (evt.IsLocalPlayer && evt.DerivedCarrierId)
                RemoveAutoWord(evt.CarrierId, "1000000A", evt);
        }

        private void HandleCarrierClear(BrawlerEvent evt)
        {
            string text = _wordTable.GetText(evt.CurrentWordTableTag, evt.CurrentWordIndex);
            evt.WordText = text;
            string message = string.Format(
                "CLEAR|carrier=0x{0:X8}|command=0x{1:X8}|table=0x{2:X8}|word={3}",
                evt.CarrierId, evt.CommandId, evt.CurrentWordTableTag,
                _wordTable.Display(evt.CurrentWordTableTag, evt.CurrentWordIndex));

            WriteEvent(evt, message);
            AppendOutput("[清除] " + message, Color.Gray);
            RemoveAutoWord(evt.CarrierId, string.Format("0x{0:X8}", evt.CommandId), evt);
        }

        private void HandleCastStart(BrawlerEvent evt)
        {
            bool forbidden = IsForbiddenTypingCast(evt.ActionId);
            string message = string.Format(
                "CAST|carrier=0x{0:X8}|action=0x{1:X4}|duration={2:F3}s|forbiddenTyping={3}",
                evt.CarrierId, evt.ActionId, evt.CastDurationSeconds,
                forbidden ? "true" : "false");

            if (!forbidden)
                return;

            WriteEvent(evt, message);
            AppendOutput("[读条] " + message, Color.FromArgb(255, 150, 100));
            PauseAutoTypingForCast(evt);
        }

        private static bool IsForbiddenTypingCast(uint actionId)
        {
            return TypingPauseController.IsStopAction(actionId);
        }

        private void WriteEvent(BrawlerEvent evt, string message)
        {
            if (evt.RawHex != null)
                _logger.WriteRaw(evt.RawHex);
            _logger.WriteLine(GetLogTag(evt), message);
        }

        private void WriteUnknown(uint tableTag, uint index, string context)
        {
            _logger.WriteLine("UNKNOWN", string.Format(
                "table=0x{0:X8}|index=0x{1:X}|context={2}", tableTag, index, context));
        }

        private static string GetLogTag(BrawlerEvent evt)
        {
            switch (evt.EventType)
            {
                case BrawlerEventType.WordAssign: return "ASSIGN";
                case BrawlerEventType.Typed: return "TYPED";
                case BrawlerEventType.Zone: return "ZONE";
                case BrawlerEventType.WordDisappear: return "DISAPPEAR";
                case BrawlerEventType.CarrierClear: return "CLEAR";
                case BrawlerEventType.CastStart: return "CAST";
                default: return "EVENT";
            }
        }

        private static string GetActionName(uint action)
        {
            switch (action)
            {
                case BrawlerOpcodes.ACTION_WORD_HIT: return "0xC16C(命中)";
                case BrawlerOpcodes.ACTION_WORD_KILL: return "0xC16D(击杀)";
                case BrawlerOpcodes.ACTION_WORD_MISS_1: return "0xC16E(未完成1)";
                case BrawlerOpcodes.ACTION_WORD_MISS_2: return "0xC16F(未完成2)";
                case BrawlerOpcodes.ACTION_WORD_SELF_PUNISH: return "0xC170(打错惩罚)";
                default: return string.Format("0x{0:X}", action);
            }
        }

        #region Auto typing

        private bool AutoTypeEnabled
        {
            get { return _chkAutoType != null && _chkAutoType.Checked; }
        }

        private void HandleAutoAssignment(BrawlerEvent evt, string text)
        {
            if (!AutoTypeEnabled) return;

            bool newRound = false;
            bool removedUnknown = false;
            bool cancelledPendingSubmit = false;

            lock (_autoLock)
            {
                DateTime now = evt.Timestamp;
                if (!_autoRoundActive || ((now - _lastAutoAssignTime).TotalSeconds > 10 &&
                    _autoWords.Count == 0 && !_autoWaitingForSubmit && !_autoInputBusy && !_autoNeedsClear))
                {
                    ClearAutoWordsLocked("new round");
                    _autoRoundActive = true;
                    newRound = true;
                }

                _lastAutoAssignTime = now;

                if (string.IsNullOrEmpty(text))
                {
                    removedUnknown = RemoveAutoWordLocked(evt.CarrierId);
                    cancelledPendingSubmit = CancelPendingSubmitLocked(evt.CarrierId);
                }
                else
                {
                    AutoWordTarget target;
                    if (_autoWords.TryGetValue(evt.CarrierId, out target) &&
                        target.AssignmentRevision > evt.AssignmentRevision)
                        return;

                    if (!_autoWords.TryGetValue(evt.CarrierId, out target))
                    {
                        target = new AutoWordTarget
                        {
                            CarrierId = evt.CarrierId,
                            Revision = 1,
                            IsActive = true
                        };
                        _autoWords[evt.CarrierId] = target;
                    }

                    if (target.AssignmentRevision != evt.AssignmentRevision ||
                        target.TableTag != evt.CurrentWordTableTag ||
                        target.WordIndex != evt.CurrentWordIndex)
                    {
                        cancelledPendingSubmit = CancelPendingSubmitLocked(evt.CarrierId);
                        target.TableTag = evt.CurrentWordTableTag;
                        target.WordIndex = evt.CurrentWordIndex;
                        target.Text = text;
                        target.Revision++;
                        target.AssignmentRevision = evt.AssignmentRevision;
                        target.QueueOrder = ++_autoWordOrder;
                        target.AssignedAt = now;
                        target.IsActive = true;
                        target.HasSubmitted = false;
                    }
                    else
                    {
                        target.Text = text;
                        target.AssignedAt = now;
                        target.IsActive = true;
                    }
                }
            }

            if (newRound)
                LogAuto("ROUND|state=start|trigger=assignment gap > 10s", Color.LightGreen);

            if (removedUnknown)
            {
                _logger.WriteLine("AUTO", string.Format(
                    "SKIP|carrier=0x{0:X8}|table=0x{1:X8}|index=0x{2:X}|reason=unknown-word",
                    evt.CarrierId, evt.CurrentWordTableTag, evt.CurrentWordIndex));
            }

            if (cancelledPendingSubmit)
            {
                _logger.WriteLine("AUTO", string.Format(
                    "CANCEL_PENDING|carrier=0x{0:X8}|reason=assignment-changed|action=queue-clear",
                    evt.CarrierId));
            }

            TryTypeNextWord("assignment");
            UpdateAutoStatus();
        }

        private void HandleAutoTypedResult(BrawlerEvent evt)
        {
            if (!AutoTypeEnabled) return;

            if (evt.IsLocalPlayer && evt.ActionId == BrawlerOpcodes.ACTION_WORD_SELF_PUNISH)
            {
                AutoWordTarget wrong = null;
                bool suppressed = false;
                bool cancelPendingSubmit = false;

                lock (_autoLock)
                {
                    wrong = _lastAutoSubmittedTarget;
                    if (wrong != null && !String.IsNullOrEmpty(wrong.Text))
                    {
                        suppressed = _autoSuppressedTexts.Add(wrong.Text);
                        _autoSuppressedKeys.Add(MakeAutoWordKey(wrong.TableTag, wrong.WordIndex));
                        RemoveSuppressedTargetsLocked();

                        if (_autoPendingSubmit != null &&
                            (String.Equals(_autoPendingSubmit.Text, wrong.Text, StringComparison.Ordinal) ||
                             MakeAutoWordKey(_autoPendingSubmit.TableTag, _autoPendingSubmit.WordIndex) ==
                             MakeAutoWordKey(wrong.TableTag, wrong.WordIndex)))
                        {
                            _autoPendingSubmit = null;
                            _autoWaitingForSubmit = false;
                            if (_autoTypeTimer != null)
                            {
                                try { _autoTypeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                                catch { }
                            }
                            cancelPendingSubmit = true;
                            RequestAutoClearLocked("wrong-text-suppressed");
                        }
                    }
                }

                if (cancelPendingSubmit)
                {
                    TryTypeNextWord("wrong-text-suppressed");
                }

                _logger.WriteLine("AUTO", string.Format(
                    "WRONG_TEXT_SUPPRESSED|action=0xC170|table=0x{0:X8}|index=0x{1:X}|word={2}|new={3}|pause=0",
                    wrong != null ? wrong.TableTag : 0,
                    wrong != null ? wrong.WordIndex : 0,
                    wrong != null ? wrong.Text : "?",
                    suppressed ? "true" : "false"));
                return;
            }

            if (!evt.IsLocalPlayer)
            {
                if (evt.ActionId == BrawlerOpcodes.ACTION_WORD_KILL)
                    _logger.WriteLine("AUTO", string.Format(
                        "KEEP|carrier=0x{0:X8}|reason=other-player-result|by=0x{1:X8}",
                        evt.CarrierId, evt.SourceActor));
                return;
            }

            if (evt.ActionId == BrawlerOpcodes.ACTION_WORD_KILL ||
                evt.ActionId == BrawlerOpcodes.ACTION_WORD_HIT)
            {
                RemoveAutoWord(evt.CarrierId, "local-word-complete", evt);
            }
        }

        private void RemoveAutoWord(uint carrierId, string reason, BrawlerEvent expected)
        {
            bool removed = false;
            bool typeNext = false;
            bool cancelPendingSubmit = false;

            lock (_autoLock)
            {
                AutoWordTarget current;
                if (_autoWords.TryGetValue(carrierId, out current) &&
                    (!expected.HasCarrierState ||
                     current.AssignmentRevision != expected.AssignmentRevision ||
                     current.TableTag != expected.CurrentWordTableTag ||
                     current.WordIndex != expected.CurrentWordIndex))
                {
                    _logger.WriteLine("AUTO", string.Format(
                        "KEEP|carrier=0x{0:X8}|reason=stale-word-event|event={1}|eventRevision={2}|currentRevision={3}",
                        carrierId, reason, expected.AssignmentRevision, current.AssignmentRevision));
                    return;
                }

                if (carrierId != 0)
                {
                    removed = RemoveAutoWordLocked(carrierId);
                    if (removed && _autoWaitingForSubmit &&
                        _autoPendingSubmit != null &&
                        _autoPendingSubmit.CarrierId == carrierId)
                    {
                        _autoWaitingForSubmit = false;
                        _autoPendingSubmit = null;
                        if (_autoTypeTimer != null)
                        {
                            try { _autoTypeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                            catch { }
                        }
                        cancelPendingSubmit = true;
                        RequestAutoClearLocked(reason);
                    }
                    typeNext = removed && !_autoWaitingForSubmit &&
                               _autoTypingCarrier == 0;
                }
            }

            if (removed)
            {
                _logger.WriteLine("AUTO", string.Format(
                    "REMOVE|carrier=0x{0:X8}|reason={1}", carrierId, reason));
                if (cancelPendingSubmit)
                    _logger.WriteLine("AUTO", string.Format(
                        "CANCEL_PENDING|carrier=0x{0:X8}|reason={1}|action=queue-clear",
                        carrierId, reason));
            }

            if (typeNext)
                TryTypeNextWord(reason);

            UpdateAutoStatus();
        }

        private bool RemoveAutoWordLocked(uint carrierId)
        {
            if (carrierId == 0) return false;

            AutoWordTarget target;
            if (!_autoWords.TryGetValue(carrierId, out target))
                return false;

            target.IsActive = false;
            _autoWords.Remove(carrierId);
            return true;
        }

        private void RemoveSubmittedWordLocked(AutoWordTarget submitted)
        {
            AutoWordTarget target;
            if (!_autoWords.TryGetValue(submitted.CarrierId, out target))
                return;

            if (target.AssignmentRevision == submitted.AssignmentRevision &&
                target.QueueOrder == submitted.QueueOrder &&
                target.TableTag == submitted.TableTag &&
                target.WordIndex == submitted.WordIndex)
            {
                target.HasSubmitted = true;
            }
        }

        private void RemoveSuppressedTargetsLocked()
        {
            List<uint> removeCarrierIds = null;
            foreach (KeyValuePair<uint, AutoWordTarget> pair in _autoWords)
            {
                AutoWordTarget target = pair.Value;
                if (!String.IsNullOrEmpty(target.Text) &&
                    (_autoSuppressedTexts.Contains(target.Text) ||
                     _autoSuppressedKeys.Contains(MakeAutoWordKey(target.TableTag, target.WordIndex))))
                {
                    if (removeCarrierIds == null)
                        removeCarrierIds = new List<uint>();
                    removeCarrierIds.Add(pair.Key);
                }
            }

            if (removeCarrierIds == null)
                return;

            foreach (uint carrierId in removeCarrierIds)
                RemoveAutoWordLocked(carrierId);
        }

        private static ulong MakeAutoWordKey(uint tableTag, uint wordIndex)
        {
            return ((ulong)tableTag << 32) | wordIndex;
        }

        private void ResetAutoRound(string reason)
        {
            lock (_autoLock)
            {
                ClearAutoWordsLocked(reason);
                _autoRoundActive = false;
                _lastAutoAssignTime = DateTime.MinValue;
            }

            LogAuto("ROUND|state=stop|reason=" + reason, Color.Orange);
            UpdateAutoStatus();
        }

        private void ClearAutoWordsLocked(string reason)
        {
            if (_autoPendingSubmit != null)
                RequestAutoClearLocked(reason);
            _autoPendingSubmit = null;
            _autoWaitingForSubmit = false;
            // round token 用于让正在输入中的旧目标失效。
            _autoRoundToken++;
            // 单词消失的 pending submit 由 RemoveAutoWord 取消。
            _autoWords.Clear();
            _autoWordOrder = 0;
            _autoSuppressedTexts.Clear();
            _autoSuppressedKeys.Clear();
            _lastAutoSubmittedTarget = null;

            // 词明确消失时取消 pending submit；清空动作只使用 Enter，
            // 本插件永远不发送 Backspace。
        }

        private bool CancelPendingAutoSubmit(string reason)
        {
            bool hadPendingSubmit = false;

            lock (_autoLock)
            {
                hadPendingSubmit = _autoWaitingForSubmit || _autoPendingSubmit != null;
                _autoWaitingForSubmit = false;
                _autoPendingSubmit = null;
                if (_autoTypeTimer != null)
                {
                    try { _autoTypeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                    catch { }
                }
                if (hadPendingSubmit || _autoNeedsClear)
                    RequestAutoClearLocked(reason);
            }

            _logger.WriteLine("AUTO", "CANCEL_PENDING|reason=" + reason);
            return hadPendingSubmit;
        }

        private void PauseAutoTypingForCast(BrawlerEvent evt)
        {
            if (_typingPause.RecordCastStart(evt.CarrierId, evt.ActionId,
                evt.CastDurationSeconds, evt.Timestamp.ToUniversalTime()))
                LogAuto(string.Format(
                    "CAST_WINDOW|action=0x{0:X4}|carrier=0x{1:X8}|castSeconds={2:F3}|pauseAfter={2:F3}s|fallbackGuard={3:F1}s",
                    evt.ActionId, evt.CarrierId, evt.CastDurationSeconds,
                    TypingPauseController.FallbackGuardSeconds), Color.Orange);
        }

        private bool CanSendAutoInput()
        {
            return _initialized && _chkEnabled != null && _chkEnabled.Checked &&
                   (_userKeyboardMonitor == null || !_userKeyboardMonitor.IsPaused) &&
                   _typingPause.RemainingMilliseconds(DateTime.UtcNow) == 0;
        }

        private void TryTypeNextWord(string trigger)
        {
            if (!_initialized || _chkEnabled == null || !_chkEnabled.Checked || !AutoTypeEnabled)
                return;

            AutoWordTarget snapshot = null;

            lock (_autoLock)
            {
                if (!_autoRoundActive || _autoWaitingForSubmit || _autoInputBusy)
                    return;

                if (_autoNeedsClear)
                {
                    ScheduleAutoRetryLocked(20);
                    return;
                }

                if (_userKeyboardMonitor != null && _userKeyboardMonitor.IsPaused)
                {
                    EnsureAutoTimerLocked();
                    _autoTypeTimer.Change(250, Timeout.Infinite);
                    return;
                }

                int blockDelayMs = _typingPause.RemainingMilliseconds(DateTime.UtcNow);
                if (blockDelayMs > 0)
                {
                    EnsureAutoTimerLocked();
                    _autoTypeTimer.Change(Math.Min(250, blockDelayMs), Timeout.Infinite);
                    return;
                }

                snapshot = SelectRandomTargetLocked();
                if (snapshot == null)
                    return;

                snapshot.RoundToken = _autoRoundToken;
                _autoTypingCarrier = snapshot.CarrierId;
                _autoInputBusy = true;
                snapshot.AttemptCount++;
            }

            UnicodeInputSender.Result result = null;
            try
            {
                result = TypeInput(snapshot.Text, 8);
            }
            catch (Exception ex)
            {
                LogAuto("TYPE|error=" + ex.Message, Color.Red);
                _logger.WriteLine("ERROR", "自动输入写入失败: " + ex);
            }

            int delayMs = 250;
            bool armed = false;
            bool typedSuccessfully = result != null && result.Success;

            lock (_autoLock)
            {
                if (_autoTypingCarrier == snapshot.CarrierId)
                    _autoTypingCarrier = 0;
                _autoInputBusy = false;

                bool pluginActive = _initialized && _chkEnabled != null &&
                                    _chkEnabled.Checked && AutoTypeEnabled;
                if (pluginActive && typedSuccessfully && snapshot.RoundToken == _autoRoundToken &&
                    IsAutoTargetCurrentLocked(snapshot))
                {
                    _autoPendingSubmit = snapshot;
                    _autoWaitingForSubmit = true;
                    delayMs = GetRandomDelayMsLocked();
                    EnsureAutoTimerLocked();
                    _autoTypeTimer.Change(delayMs, Timeout.Infinite);
                    armed = true;
                }
                else
                {
                    if (result != null && (result.Success || result.HasTypedInput))
                        RequestAutoClearLocked("type-not-armed");
                    if (pluginActive)
                        ScheduleAutoRetryLocked(250);
                }
            }

            if (result != null)
            {
                LogAuto(string.Format(
                    "TYPE|trigger={0}|carrier=0x{1:X8}|word={2}|attempt={3}|typed={4}|reason={5}|wait={6:F1}s|inputMs={7}|enterGateMs={8}",
                    trigger, snapshot.CarrierId, snapshot.Text, snapshot.AttemptCount,
                    result.Success ? "true" : "false", result.Reason, delayMs / 1000.0,
                    result.ElapsedMs, result.EnterGateMs),
                    result.Success ? Color.LightGreen : Color.Red);
                _logger.WriteLine("AUTO", string.Format(
                    "TYPE|carrier=0x{0:X8}|table=0x{1:X8}|index=0x{2:X}|word={3}|attempt={4}|typed={5}|reason={6}|wait={7:F1}|inputMs={8}|enterGateMs={9}|foreground={10}|window={11}|armed={12}|revision={13}",
                    snapshot.CarrierId, snapshot.TableTag, snapshot.WordIndex, snapshot.Text,
                    snapshot.AttemptCount, result.Success ? "true" : "false", result.Reason,
                    delayMs / 1000.0, result.ElapsedMs, result.EnterGateMs,
                    result.ForegroundProcess ?? "?", result.ForegroundWindowTitle ?? "?",
                    armed, snapshot.AssignmentRevision));
            }

            UpdateAutoStatus();
        }

        private void OnAutoTypeTimer(object state)
        {
            AutoWordTarget snapshot = null;
            bool retryType = false;
            bool staleRound = false;
            bool clearInput = false;

            lock (_autoLock)
            {
                if (!_initialized || _chkEnabled == null || !_chkEnabled.Checked ||
                    (!AutoTypeEnabled && !_autoNeedsClear))
                    return;

                if (_autoInputBusy)
                    return;

                if (_autoNeedsClear)
                {
                    if ((_userKeyboardMonitor != null && _userKeyboardMonitor.IsPaused) ||
                        _typingPause.RemainingMilliseconds(DateTime.UtcNow) > 0)
                    {
                        ScheduleAutoRetryLocked(250);
                        return;
                    }
                    clearInput = true;
                    _autoNeedsClear = false;
                    _autoInputBusy = true;
                    _lastAutoSubmittedTarget = null;
                }

                else if (!_autoWaitingForSubmit)
                {
                    // TypeText 失败后的 250ms 重试，或其他空闲定时器到期。
                    // 事件线程本身也会调用 TryTypeNextWord，这里只做兜底。
                    _autoTypeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    retryType = true;
                }
                else
                {
                    if (_userKeyboardMonitor != null && _userKeyboardMonitor.IsPaused)
                    {
                        _autoTypeTimer.Change(250, Timeout.Infinite);
                        return;
                    }

                    int remaining = _typingPause.RemainingMilliseconds(DateTime.UtcNow);
                    if (remaining > 0)
                    {
                        _autoTypeTimer.Change(Math.Min(250, remaining), Timeout.Infinite);
                        return;
                    }

                    snapshot = _autoPendingSubmit;
                    _autoPendingSubmit = null;
                    _autoWaitingForSubmit = false;
                    staleRound = snapshot.RoundToken != _autoRoundToken;
                    _autoInputBusy = true;
                }
            }

            if (clearInput)
            {
                UnicodeInputSender.Result clearResult = null;
                try { clearResult = SubmitInput(); }
                catch (Exception ex) { _logger.WriteLine("ERROR", "Clear input failed: " + ex); }
                lock (_autoLock)
                {
                    _autoInputBusy = false;
                    if (clearResult == null || !clearResult.Submitted)
                        _autoNeedsClear = true;
                    if (_autoNeedsClear)
                        ScheduleAutoRetryLocked(250);
                }
                _logger.WriteLine("AUTO", string.Format(
                    "CLEAR_INPUT|submitted={0}|reason={1}",
                    clearResult != null && clearResult.Submitted,
                    clearResult != null ? clearResult.Reason : "exception"));
                if (clearResult != null && clearResult.Submitted)
                    TryTypeNextWord("after-clear");
                UpdateAutoStatus();
                return;
            }

            if (retryType)
            {
                TryTypeNextWord("timer-retry");
                UpdateAutoStatus();
                return;
            }

            if (snapshot == null)
                return;

            if (staleRound)
            {
                LogAuto(string.Format(
                    "DROP_STALE|carrier=0x{0:X8}|word={1}|reason=round-changed",
                    snapshot.CarrierId, snapshot.Text), Color.Orange);
                lock (_autoLock)
                {
                    _autoInputBusy = false;
                    RequestAutoClearLocked("round-changed");
                }
                TryTypeNextWord("round-changed");
                UpdateAutoStatus();
                return;
            }

            UnicodeInputSender.Result result = null;
            try
            {
                lock (_autoLock)
                {
                    _lastAutoSubmittedTarget = snapshot;
                }
                result = SubmitInput();
            }
            catch (Exception ex)
            {
                LogAuto("SUBMIT|error=" + ex.Message, Color.Red);
                _logger.WriteLine("ERROR", "自动输入提交失败: " + ex);
            }

            if (result != null)
            {
                LogAuto(string.Format(
                    "SUBMIT|carrier=0x{0:X8}|word={1}|attempt={2}|submitted={3}|reason={4}|enterMs={5}|enterGateMs={6}",
                    snapshot.CarrierId, snapshot.Text, snapshot.AttemptCount,
                    result.Submitted ? "true" : "false", result.Reason, result.ElapsedMs,
                    result.EnterGateMs),
                    result.Success ? Color.LightGreen : Color.Red);
                _logger.WriteLine("AUTO", string.Format(
                    "SUBMIT|carrier=0x{0:X8}|table=0x{1:X8}|index=0x{2:X}|word={3}|submitted={4}|reason={5}|enterMs={6}|enterGateMs={7}|foreground={8}|window={9}",
                    snapshot.CarrierId, snapshot.TableTag, snapshot.WordIndex, snapshot.Text,
                    result.Submitted ? "true" : "false", result.Reason, result.ElapsedMs,
                    result.EnterGateMs, result.ForegroundProcess ?? "?",
                    result.ForegroundWindowTitle ?? "?"));
            }

            lock (_autoLock)
            {
                _autoInputBusy = false;
                if (result != null && result.Submitted)
                {
                    RemoveSubmittedWordLocked(snapshot);
                    _logger.WriteLine("AUTO", string.Format(
                        "CONSUME|carrier=0x{0:X8}|reason=submit-once", snapshot.CarrierId));
                }
                else if (snapshot.RoundToken == _autoRoundToken && AutoTypeEnabled &&
                         IsAutoTargetCurrentLocked(snapshot))
                {
                    _autoPendingSubmit = snapshot;
                    _autoWaitingForSubmit = true;
                    ScheduleAutoRetryLocked(250);
                }
                else
                    RequestAutoClearLocked("submit-failed-stale");
            }

            // Keep submitted assignments until their completion/replacement so a
            // repeated assignment cannot cause the same prompt to be submitted twice.
            TryTypeNextWord("after-submit");
            UpdateAutoStatus();
        }

        private AutoWordTarget SelectRandomTargetLocked()
        {
            List<AutoWordTarget> targets = new List<AutoWordTarget>();
            foreach (var pair in _autoWords)
            {
                AutoWordTarget target = pair.Value;
                if (target.IsActive && !target.HasSubmitted &&
                    !string.IsNullOrEmpty(target.Text) &&
                    !_autoSuppressedTexts.Contains(target.Text) &&
                    !_autoSuppressedKeys.Contains(MakeAutoWordKey(target.TableTag, target.WordIndex)))
                    targets.Add(target);
            }

            if (targets.Count == 0)
                return null;

            AutoWordTarget selected = targets[_autoRandom.Next(targets.Count)];
            return new AutoWordTarget
            {
                CarrierId = selected.CarrierId,
                TableTag = selected.TableTag,
                WordIndex = selected.WordIndex,
                Text = selected.Text,
                QueueOrder = selected.QueueOrder,
                Revision = selected.Revision,
                AssignmentRevision = selected.AssignmentRevision,
                AssignedAt = selected.AssignedAt,
                AttemptCount = selected.AttemptCount,
                IsActive = selected.IsActive
            };
        }

        private int GetRandomDelayMsLocked()
        {
            int minTenths = (int)Math.Floor(_autoMinDelaySeconds * 10m + 0.000001m);
            int maxTenths = (int)Math.Floor(_autoMaxDelaySeconds * 10m + 0.000001m);
            if (maxTenths < minTenths) maxTenths = minTenths;

            return _autoRandom.Next(minTenths, maxTenths + 1) * 100;
        }

        private void EnsureAutoTimerLocked()
        {
            if (_autoTypeTimer != null) return;
            _autoTypeTimer = new System.Threading.Timer(
                OnAutoTypeTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        private void ScheduleAutoRetryLocked(int delayMs)
        {
            EnsureAutoTimerLocked();
            _autoTypeTimer.Change(delayMs, Timeout.Infinite);
        }

        private void RequestAutoClearLocked(string reason)
        {
            _autoNeedsClear = true;
            _logger.WriteLine("AUTO", "CLEAR_PENDING|reason=" + reason);
            ScheduleAutoRetryLocked(20);
        }

        private void StartAutoTyping()
        {
            lock (_autoLock)
            {
                ClearAutoWordsLocked("auto enabled");
                _autoRoundActive = false;
                _lastAutoAssignTime = DateTime.MinValue;
                _autoWaitingForSubmit = false;
                _autoPendingSubmit = null;
                EnsureAutoTimerLocked();
            }

            _logger.WriteLine("INFO", string.Format(
                "自动输入启用: type-to-submit delay={0:F1}s~{1:F1}s",
                _autoMinDelaySeconds, _autoMaxDelaySeconds));
            _logger.WriteLine("INFO", "自动输入顺序: 逗号 + 200ms + Unicode 正文 + Enter + 100ms 循环冷却");
            _logger.WriteLine("INFO", string.Format(
                "Enter 输出限速: 任意两次 Enter 间隔 >= {0:F1}s",
                _autoMinDelaySeconds));
            UpdateAutoStatus();
        }

        private void StopAutoTyping()
        {
            ResetAutoRound("auto disabled");
            CancelPendingAutoSubmit("auto disabled");
        }

        private void DisposeAutoTimer()
        {
            System.Threading.Timer timer = null;
            lock (_autoLock)
            {
                if (_autoTypeTimer == null) return;

                timer = _autoTypeTimer;
                _autoTypeTimer = null;
                _autoWaitingForSubmit = false;
                _autoPendingSubmit = null;
                _autoNeedsClear = false;
            }

            try { timer.Dispose(); }
            catch { }
        }

        private void LogAuto(string message, Color color)
        {
            _logger.WriteLine("AUTO", message);
            AppendOutput("[自动] " + message, color);
        }

        private void UpdateAutoStatus()
        {
            if (_lblAutoStatus == null) return;

            int count = 0;
            bool typing = false;
            bool waiting = false;
            bool blocked = false;
            bool manual = false;
            lock (_autoLock)
            {
                foreach (var pair in _autoWords)
                    if (pair.Value.IsActive) count++;
                typing = _autoTypingCarrier != 0;
                waiting = _autoWaitingForSubmit;
                blocked = _typingPause.RemainingMilliseconds(DateTime.UtcNow) > 0;
                manual = _userKeyboardMonitor != null && _userKeyboardMonitor.IsPaused;
            }

            string text;
            if (!AutoTypeEnabled)
                text = "自动输入: 未启用";
            else if (typing)
                text = "自动输入: 正在写入";
            else if (waiting)
                text = "自动输入: 等待提交延迟";
            else if (manual)
                text = "自动输入: 手动输入暂停";
            else if (blocked)
                text = "自动输入: 禁止打字窗口中";
            else
                text = "自动输入: 待词/" + count;

            text += string.Format("，输入后延迟 {0:F1}~{1:F1}s", _autoMinDelaySeconds, _autoMaxDelaySeconds);
            SetAutoStatusText(text);
        }

        private void SetAutoStatusText(string text)
        {
            try
            {
                if (_lblAutoStatus.InvokeRequired)
                {
                    _lblAutoStatus.BeginInvoke(new Action(() => _lblAutoStatus.Text = text));
                    return;
                }

                _lblAutoStatus.Text = text;
            }
            catch { }
        }


        #endregion

        #endregion

        #region Enable / buttons / timer

        private void OnEnabled()
        {
            if (!_initialized) return;

            StartLogger();
            TryHookFfxivPlugin(false);
            if (!_ffxivHooked)
            {
                AppendOutput("[回退] 二进制事件未就绪时将解析 ACT type 252 网络日志", Color.Yellow);
            }

            _timerTicks = 0;
            _hookTimer.Start();
            if (_statusLabel != null)
                _statusLabel.Text = "魔光键影监听中";
        }

        private void OnDisabled()
        {
            if (AutoTypeEnabled)
                StopAutoTyping();
            if (_hookTimer != null) _hookTimer.Stop();
            UnhookFfxivPlugin();
            SaveUnknown(true);
            StopLogger();
            if (_statusLabel != null)
                _statusLabel.Text = "魔光键影已停止";
        }

        private void StartLogger()
        {
            if (!_chkWriteLog.Checked) return;
            if (!Directory.Exists(_logDirectory)) Directory.CreateDirectory(_logDirectory);
            _logger.Open(_logDirectory);
            _logger.WriteLine("INFO", "插件版本: " + Assembly.GetExecutingAssembly().GetName().Version);
            _logger.WriteLine("INFO", "插件目录: " + _pluginDirectory);
            _logger.WriteLine("INFO", "词表: " + _wordsFile);
            _logger.WriteLine("INFO", _wordTable.GetStatus());
            foreach (string warning in _wordTable.Warnings)
                _logger.WriteLine("WARN", warning);

            UpdateFileLabels();
            AppendOutput("[日志] " + _logger.LogFilePath, Color.Gray);
        }

        private void StopLogger()
        {
            if (_logger == null) return;
            _logger.Close();
            UpdateFileLabels();
        }

        private void OnHookTimerTick(object sender, EventArgs e)
        {
            if (!_initialized || !_chkEnabled.Checked) return;

            if (!_ffxivHooked)
                TryHookFfxivPlugin(true);

            _timerTicks++;
            if (AutoTypeEnabled)
            {
                bool stale = false;
                lock (_autoLock)
                {
                    stale = _autoRoundActive && _autoWords.Count == 0 &&
                            !_autoInputBusy && !_autoWaitingForSubmit && !_autoNeedsClear &&
                            (DateTime.Now - _lastAutoAssignTime).TotalSeconds > 30;
                }
                if (stale)
                    ResetAutoRound("assignment timeout 30s");
            }

            if (_timerTicks >= 15)
            {
                _timerTicks = 0;
                if (_wordTable.Dirty)
                    SaveUnknown(true);
            }
        }

        private void ChkEnabled_CheckedChanged(object sender, EventArgs e)
        {
            if (!_initialized) return;
            if (_chkEnabled.Checked) OnEnabled();
            else OnDisabled();
        }

        private void ChkWriteLog_CheckedChanged(object sender, EventArgs e)
        {
            if (!_initialized || !_chkEnabled.Checked) return;
            if (_chkWriteLog.Checked) StartLogger();
            else StopLogger();
        }

        private void ChkAutoType_CheckedChanged(object sender, EventArgs e)
        {
            if (!_initialized) return;
            if (_chkAutoType.Checked) StartAutoTyping();
            else StopAutoTyping();
        }

        private void DelaySetting_ValueChanged(object sender, EventArgs e)
        {
            if (_nudMinDelay != null && _nudMaxDelay != null &&
                _nudMinDelay.Value > _nudMaxDelay.Value)
            {
                // 避免用户把最小值调到最大值以上。
                if (ReferenceEquals(sender, _nudMinDelay))
                    _nudMaxDelay.Value = _nudMinDelay.Value;
                else
                    _nudMinDelay.Value = _nudMaxDelay.Value;
                return;
            }

            if (_settingsLoading)
                return;

            UpdateDelaySettingsFromControls();
            if (_settings != null)
                SaveSettings();
            if (_logger != null)
                _logger.WriteLine("INFO", string.Format(
                    "自动输入延迟设置更新: {0:F1}s~{1:F1}s",
                    _autoMinDelaySeconds, _autoMaxDelaySeconds));
        }

        private bool CancelPendingSubmitLocked(uint carrierId)
        {
            if (!_autoWaitingForSubmit || _autoPendingSubmit == null ||
                _autoPendingSubmit.CarrierId != carrierId)
                return false;

            _autoWaitingForSubmit = false;
            _autoPendingSubmit = null;
            if (_autoTypeTimer != null)
            {
                try { _autoTypeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch { }
            }
            RequestAutoClearLocked("assignment-changed");
            return true;
        }

        private bool IsAutoTargetCurrentLocked(AutoWordTarget target)
        {
            if (target == null)
                return false;

            AutoWordTarget current;
            return _autoWords.TryGetValue(target.CarrierId, out current) &&
                   current.IsActive && !current.HasSubmitted &&
                   current.QueueOrder == target.QueueOrder &&
                   current.AssignmentRevision == target.AssignmentRevision &&
                   current.TableTag == target.TableTag &&
                   current.WordIndex == target.WordIndex;
        }

        private void UpdateDelaySettingsFromControls()
        {
            if (_nudMinDelay == null || _nudMaxDelay == null) return;

            decimal min = _nudMinDelay.Value;
            decimal max = _nudMaxDelay.Value;
            if (max < min) max = min;

            _autoMinDelaySeconds = min;
            _autoMaxDelaySeconds = max;
            UnicodeInputSender.MinimumEnterIntervalMs =
                (int)Math.Ceiling(min * 1000m);
            UpdateAutoStatus();
        }

        private void BtnReloadWords_Click(object sender, EventArgs e)
        {
            _wordTable.Reload(_wordsFile);
            SaveUnknown(true);
            UpdateWordStatus();
            AppendOutput("[词表] 已重载: " + _wordTable.GetStatus(), Color.Gray);
            foreach (string warning in _wordTable.Warnings)
                AppendOutput("[词表] " + warning, Color.Orange);
            _logger.WriteLine("INFO", "词表重载: " + _wordTable.GetStatus());
        }

        private void BtnSaveUnknown_Click(object sender, EventArgs e)
        {
            SaveUnknown(false);
        }

        private void BtnOpenWords_Click(object sender, EventArgs e)
        {
            if (!File.Exists(_wordsFile))
            {
                AppendOutput("[词表] 文件不存在: " + _wordsFile, Color.Red);
                return;
            }
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _wordsFile + "\"");
        }

        private void BtnOpenLogs_Click(object sender, EventArgs e)
        {
            if (!Directory.Exists(_logDirectory)) Directory.CreateDirectory(_logDirectory);
            System.Diagnostics.Process.Start("explorer.exe", _logDirectory);
        }

        private void BtnTestInput_Click(object sender, EventArgs e)
        {
            if (_testInputTimer != null) return;

            bool autoBusy;
            lock (_autoLock)
            {
                autoBusy = _autoInputBusy || _autoWaitingForSubmit || _autoNeedsClear;
            }

            if (autoBusy)
            {
                AppendOutput("[测试] 自动输入正在等待或发送，请稍后再试", Color.Orange);
                return;
            }

            _testInputCountdown = 3;
            _btnTestInput.Enabled = false;
            _btnTestInput.Text = "3秒后输入";
            _testInputTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _testInputTimer.Tick += TestInputTimer_Tick;
            _testInputTimer.Start();

            AppendOutput("[测试] 3 秒后发送“,啦哩吼”；请立刻切换到 FF14 前台", Color.Gray);
            _logger.WriteLine("AUTO", "TEST|state=countdown|seconds=3|prefix=comma|text=啦哩吼|submit=false");
        }

        private void TestInputTimer_Tick(object sender, EventArgs e)
        {
            _testInputCountdown--;
            if (_testInputCountdown > 0)
            {
                _btnTestInput.Text = _testInputCountdown + "秒后输入";
                return;
            }

            bool autoBusy;
            lock (_autoLock)
            {
                autoBusy = _autoInputBusy || _autoWaitingForSubmit || _autoNeedsClear;
            }

            if (autoBusy)
            {
                _testInputTimer.Stop();
                _testInputTimer.Dispose();
                _testInputTimer = null;
                _btnTestInput.Enabled = true;
                _btnTestInput.Text = "测试输入";
                AppendOutput("[测试] 自动输入已接管，本次测试已取消", Color.Orange);
                _logger.WriteLine("AUTO", "TEST|state=cancelled|reason=auto-input-active");
                return;
            }

            _testInputTimer.Stop();
            _testInputTimer.Dispose();
            _testInputTimer = null;
            _btnTestInput.Enabled = true;
            _btnTestInput.Text = "测试输入";

            UnicodeInputSender.Result result = UnicodeInputSender.TypeText(
                "啦哩吼", 8, CanSendAutoInput);

            string message = string.Format(
                "TEST|state=finished|prefix=comma|text=啦哩吼|submit=false|success={0}|reason={1}|foreground={2}",
                result.Success, result.Reason,
                result.ForegroundProcess ?? "?");
            _logger.WriteLine("AUTO", message);
            AppendOutput("[测试] " + message,
                result.Success ? Color.LightGreen : Color.Red);
        }

        private void SaveUnknown(bool quiet)
        {
            bool ok = _wordTable.SaveUnknown(_unknownFile);
            if (!quiet)
                AppendOutput(ok ? "[未知] 已保存: " + _unknownFile : "[未知] 保存失败: " + _unknownFile,
                    ok ? Color.Gray : Color.Red);
            if (!ok)
                _logger.WriteLine("ERROR", "未知词记录保存失败: " + _unknownFile);
            UpdateWordStatus();
        }

        #endregion

        #region Settings

        private void LoadSettings()
        {
            _settings.AddControlSetting(_chkEnabled.Name, _chkEnabled);
            _settings.AddControlSetting(_chkWriteLog.Name, _chkWriteLog);
            _settings.AddControlSetting(_chkRawHex.Name, _chkRawHex);
            _settings.AddControlSetting(_chkAllPlayers.Name, _chkAllPlayers);
            _settings.AddControlSetting(_chkAutoType.Name, _chkAutoType);
            _settings.AddControlSetting(_nudMinDelay.Name, _nudMinDelay);
            _settings.AddControlSetting(_nudMaxDelay.Name, _nudMaxDelay);

            if (!File.Exists(_settingsFile)) return;

            using (FileStream stream = new FileStream(_settingsFile, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))
            {
                var document = new XmlDocument();
                document.Load(stream);

                // ACT's serializer cannot import controls that have been removed.
                foreach (XmlNode obsolete in document.SelectNodes(
                    "/Config/SettingsSerializer/CheckBox[@Name='chkPreEnter' or @Name='chkRandomWrongText']"))
                {
                    obsolete.ParentNode.RemoveChild(obsolete);
                }

                using (XmlTextReader reader = new XmlTextReader(new StringReader(document.OuterXml)))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element &&
                            reader.LocalName == "SettingsSerializer")
                        {
                            _settings.ImportFromXml(reader);
                        }
                    }
                }
            }
        }

        private void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile));
            using (FileStream stream = new FileStream(_settingsFile, FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite))
            using (XmlTextWriter writer = new XmlTextWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Formatting = Formatting.Indented;
                writer.Indentation = 1;
                writer.IndentChar = '\t';
                writer.WriteStartDocument(true);
                writer.WriteStartElement("Config");
                writer.WriteStartElement("SettingsSerializer");
                _settings.ExportToXml(writer);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        #endregion

        private string GetPluginDirectory()
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(assemblyPath))
                return Path.GetDirectoryName(assemblyPath);

            foreach (var plugin in ActGlobals.oFormActMain.ActPlugins)
            {
                if (plugin.pluginObj == this && plugin.pluginFile != null)
                    return Path.GetDirectoryName(plugin.pluginFile.FullName);
            }

            return ActGlobals.oFormActMain.AppDataFolder.FullName;
        }
    }
}
