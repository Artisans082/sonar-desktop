using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Sonar.Controls;
using Sonar.Models;
using Sonar.Rendering;
using Sonar.Services;
using Sonar.Views;

namespace Sonar;

public partial class MainWindow : Window
{
    private sealed class ReportStreamPump
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _sync = new();
        private bool _dirty;

        public void Append(string text)
        {
            lock (_sync)
            {
                _buffer.Append(text);
                _dirty = true;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _buffer.Clear();
                _dirty = true;
            }
        }

        public string Take()
        {
            lock (_sync)
            {
                if (!_dirty) return "";
                var text = _buffer.ToString();
                _buffer.Clear();
                _dirty = false;
                return text;
            }
        }

        public string Snapshot()
        {
            lock (_sync) return _buffer.ToString();
        }
    }

    private enum ActivityLevel
    {
        Info,
        Tool,
        Result,
        Think,
        Error,
    }

    private readonly ConfigManager _config;
    private readonly ObservableCollection<ActivityItem> _activities = new();
    private readonly ReportStreamPump _streamPump = new();
    private readonly DispatcherTimer _streamTimer;
    private CancellationTokenSource? _cts;
    private bool _analyzing;
    private bool _uiReady;
    private bool _syncingTime;
    private bool _syncingNumbers;
    private bool _loadingConfig;
    private string _mode = "keyword";
    private string _keyword = "";
    private string _platform = "";
    private string _query = "";
    private string _analysisReport = "";
    private Dictionary<string, long>? _lastUsage;
    private Dictionary<string, object?>? _biliData;
    private Dictionary<string, object?>? _tiebaData;
    private int _streamChars;
    private int _streamResetPending;
    private ScrollViewer? _rawScroll;
    private bool _rawAtBottom = true;
    private int _renderVersion;
    private int _historyVersion;
    private string _renderedText = "";
    private List<MdBlock>? _renderedBlocks;
    private FlowDocument? _renderedDocument;
    private int _toastVersion;

    public MainWindow()
    {
        InitializeComponent();

        _config = new ConfigManager();

        ActivityList.ItemsSource = _activities;

        _streamTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _streamTimer.Tick += OnStreamTick;
        _streamTimer.Start();

        RawReport.Loaded += OnRawReportLoaded;
        RawReport.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnRawReportScrollChanged));

        HookNumberSync(BiliPages, BiliPagesSetting);
        HookNumberSync(BiliComments, BiliCommentsSetting);
        HookNumberSync(TiebaPages, TiebaPagesSetting);
        HookNumberSync(TiebaReplies, TiebaRepliesSetting);

        _uiReady = true;
        LoadConfigToUi();
        SwitchMode("keyword");
        ShowPage("main");
        UpdateModelStatus();
        UpdateWindowLabels();

        Log("声呐 Sonar（C# / WPF）已启动");
        Log("配置目录： " + AppPaths.DataDir);
        var sources = CookieExtractor.BrowserSources();
        if (sources.Count > 0)
        {
            Log("检测到浏览器资料：" + string.Join("；", sources), "info");
        }
        AddActivity("◈", "欢迎使用声呐 Sonar，配置 LLM API 后即可开始分析", ActivityLevel.Info);
    }

    // ================================================================
    // 窗口 / 导航 / 模式
    // ================================================================

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || sender is not RadioButton { IsChecked: true, Tag: string page }) return;
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        MainPage.Visibility = page == "main" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "history") RefreshHistory();
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || sender is not RadioButton { IsChecked: true, Tag: string mode }) return;
        SwitchMode(mode);
    }

    private void SwitchMode(string mode)
    {
        _mode = mode;
        var isSingle = mode == "single_video";
        var isKeyword = mode == "keyword";

        KeywordPanel.Visibility = isSingle ? Visibility.Collapsed : Visibility.Visible;
        VideoPanel.Visibility = isSingle ? Visibility.Visible : Visibility.Collapsed;
        TiebaCheck.Visibility = isKeyword ? Visibility.Visible : Visibility.Collapsed;
        TiebaDepthPanel.Visibility = isKeyword ? Visibility.Visible : Visibility.Collapsed;
        BiliCheck.IsEnabled = !isSingle;
        TiebaCheck.IsEnabled = isKeyword;

        ModeDescText.Text = mode switch
        {
            "single_video" => "深挖单个视频的舆论偏向与情绪",
            "bili_overview" => "横向总结相关视频的观点与情绪",
            _ => "检索 B站 + 贴吧，综合研判舆论",
        };
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_analyzing)
        {
            RequestCancel();
            return;
        }
        _ = StartAnalysisAsync();
    }

    // ================================================================
    // 配置读写
    // ================================================================

    private void LoadConfigToUi()
    {
        _loadingConfig = true;
        BiliSessBox.Text = _config.GetCookie("bilibili", "SESSDATA");
        BiliJctBox.Text = _config.GetCookie("bilibili", "bili_jct");
        BiliUidBox.Text = _config.GetCookie("bilibili", "DedeUserID");
        TiebaBdussBox.Text = _config.GetCookie("tieba", "BDUSS");
        TiebaStokenBox.Text = _config.GetCookie("tieba", "STOKEN");
        var apiBase = _config.GetString("llm", "api_base", "https://api.deepseek.com/v1");
        ApiBaseBox.Text = apiBase.Length > 0 ? apiBase : "https://api.deepseek.com/v1";
        ApiKeyBox.Password = _config.GetString("llm", "api_key");
        var model = _config.GetString("llm", "model", "deepseek-chat");
        ModelBox.Text = model.Length > 0 ? model : "deepseek-chat";

        SetNumber(BiliPages, _config.GetInt("collect", "bilibili_max_pages", 2));
        SetNumber(BiliComments, _config.GetInt("collect", "bilibili_comments_per_video", 30));
        SetNumber(TiebaPages, _config.GetInt("collect", "tieba_max_pages", 3));
        SetNumber(TiebaReplies, _config.GetInt("collect", "tieba_replies_per_post", 20));
        SetNumber(BiliPagesSetting, _config.GetInt("collect", "bilibili_max_pages", 2));
        SetNumber(BiliCommentsSetting, _config.GetInt("collect", "bilibili_comments_per_video", 30));
        SetNumber(TiebaPagesSetting, _config.GetInt("collect", "tieba_max_pages", 3));
        SetNumber(TiebaRepliesSetting, _config.GetInt("collect", "tieba_replies_per_post", 20));

        var (deepBili, deepTieba) = _config.DeepCounts();
        SetNumber(DeepBiliSetting, deepBili);
        SetNumber(DeepTiebaSetting, deepTieba);
        SetNumber(VariantsSetting, _config.MaxVariants());

        var (interval, jitter, cooldown) = _config.TiebaPacing();
        IntervalSetting.Value = ClampDecimal(interval, 1, 60);
        JitterSetting.Value = ClampDecimal(jitter, 0, 10);
        CooldownSetting.Value = ClampDecimal(cooldown, 10, 600);

        var sort = _config.TiebaSearchSort();
        SortHot.IsChecked = sort == 2;
        SortRelevance.IsChecked = sort == 0;
        SortTime.IsChecked = sort == 1;
        HotFirstCheck.IsChecked = _config.TiebaHotFirst();
        HotTopicsSetting.Value = Math.Clamp(_config.TiebaHotTopics(), 1, 3);

        var window = _config.TimeWindow();
        ApplyWindowToMain(window);
        ApplyWindowToSettings(window);
        UpdateWindowLabels();

        _loadingConfig = false;
    }

    private static decimal ClampDecimal(double value, double min, double max) =>
        (decimal)Math.Clamp(double.IsFinite(value) ? value : min, min, max);

    private static void SetNumber(NumericBox box, decimal value) => box.SetValueSilently(value);

    private void UpdateConfigFromUi()
    {
        _config.SetCookie("bilibili", "SESSDATA", BiliSessBox.Text);
        _config.SetCookie("bilibili", "bili_jct", BiliJctBox.Text);
        _config.SetCookie("bilibili", "DedeUserID", BiliUidBox.Text);
        _config.SetCookie("tieba", "BDUSS", TiebaBdussBox.Text);
        _config.SetCookie("tieba", "STOKEN", TiebaStokenBox.Text);

        _config.Set("llm", "api_base", ApiBaseBox.Text.Trim());
        _config.Set("llm", "api_key", ApiKeyBox.Password.Trim());
        _config.Set("llm", "model", ModelBox.Text.Trim());

        _config.Set("collect", "bilibili_max_pages", BiliPages.IntValue);
        _config.Set("collect", "bilibili_comments_per_video", BiliComments.IntValue);
        _config.Set("collect", "tieba_max_pages", TiebaPages.IntValue);
        _config.Set("collect", "tieba_replies_per_post", TiebaReplies.IntValue);
        _config.Set("collect", "deep_bilibili", DeepBiliSetting.IntValue);
        _config.Set("collect", "deep_tieba", DeepTiebaSetting.IntValue);
        _config.Set("collect", "max_variants", VariantsSetting.IntValue);
        _config.Set("collect", "tieba_request_interval", (double)IntervalSetting.Value);
        _config.Set("collect", "tieba_interval_jitter", (double)JitterSetting.Value);
        _config.Set("collect", "tieba_block_cooldown", (double)CooldownSetting.Value);
        _config.Set("collect", "tieba_search_sort",
            SortHot.IsChecked == true ? 2 : SortRelevance.IsChecked == true ? 0 : 1);
        _config.Set("collect", "tieba_hot_first", HotFirstCheck.IsChecked == true);
        _config.Set("collect", "tieba_hot_topics", HotTopicsSetting.IntValue);
        _config.Set("collect", "time_window", MainWindowSpec().ToJson());

        UpdateModelStatus();
    }

    private void UpdateModelStatus()
    {
        var hasKey = !string.IsNullOrWhiteSpace(ApiKeyBox.Password);
        var model = ModelBox.Text.Trim();
        ModelStatusText.Text = hasKey
            ? (model.Length > 0 ? model : "已配置")
            : "未配置 API Key";
        ModelBaseText.Text = ApiBaseBox.Text.Trim();
        LlmDot.Fill = hasKey
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("WarningBrush");
    }

    private void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromUi();
        try
        {
            _config.Save();
            Log("配置已保存", "success");
            ShowToast("配置已保存");
        }
        catch (Exception ex)
        {
            Log("配置保存失败：" + ex.Message, "error");
            ShowTextViewer("配置保存失败",
                $"无法写入配置文件：\n{ex.Message}\n\n请确认目录 {AppPaths.DataDir} 存在且有写入权限。");
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split('|');
        if (parts.Length != 2) return;
        ApiBaseBox.Text = parts[0];
        ModelBox.Text = parts[1];
    }

    private void OnTestBilibiliClick(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromUi();
        Log("正在测试 B站连接...");
        _ = Task.Run(async () =>
        {
            try
            {
                using var collector = new BilibiliCollector(_config.BilibiliCookieString);
                var hot = await collector.GetHotSearchesAsync().ConfigureAwait(false);
                Post(() =>
                {
                    Log(hot.Count > 0
                        ? $"B站连接成功！热搜: {string.Join(", ", hot.Take(5))}"
                        : "B站连接成功（热搜为空）", "success");
                    SetStatus("B站连接正常");
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    Log("B站连接失败: " + ex.Message, "error");
                    SetStatus("B站连接失败");
                });
            }
        });
    }

    private void OnTestTiebaClick(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromUi();
        Log("正在测试贴吧连接...");
        _ = Task.Run(async () =>
        {
            try
            {
                using var collector = MakeTiebaCollector();
                var posts = await collector.GetBarPostsAsync("测试", 1).ConfigureAwait(false);
                Post(() =>
                {
                    if (posts.Count > 0)
                    {
                        Log($"贴吧连接成功（走线：{collector.RequestRoute}）！获取到 {posts.Count} 条帖子",
                            "success");
                        SetStatus("贴吧连接正常");
                    }
                    else
                    {
                        Log("贴吧可访问，但未解析到帖子（页面结构可能已变化）", "warning");
                        SetStatus("贴吧连接异常");
                    }
                });
            }
            catch (TiebaBlockedException ex)
            {
                Post(() =>
                {
                    Log($"贴吧被安全验证拦截：{ex.Message}", "error");
                    SetStatus("贴吧被安全验证拦截");
                    ShowTextViewer("贴吧请求被拦截", TiebaCollector.BlockedMessage() +
                        "\n\n原始信息：" + ex.Message);
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    Log("贴吧连接失败: " + ex.Message, "error");
                    SetStatus("贴吧连接失败");
                });
            }
        });
    }

    private void OnTestLlmClick(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromUi();
        if (!_config.IsLlmConfigured)
        {
            Log("请先填写 LLM API Key", "error");
            return;
        }
        Log("正在测试 LLM 连接...");
        _ = Task.Run(async () =>
        {
            try
            {
                using var analyzer = new LlmAnalyzer(
                    _config.GetString("llm", "api_base"),
                    _config.GetString("llm", "api_key"),
                    _config.GetString("llm", "model"));
                var reply = await analyzer.ChatAsync("你是测试助手", "请回复'连接成功'四个字",
                    0, 20).ConfigureAwait(false);
                Post(() =>
                {
                    Log($"LLM连接成功！模型回复: {reply}", "success");
                    SetStatus("LLM连接正常");
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    Log("LLM连接失败: " + ex.Message, "error");
                    SetStatus("LLM连接失败");
                });
            }
        });
    }

    // ================================================================
    // Cookie
    // ================================================================

    private void OnFetchBilibiliCookieClick(object sender, RoutedEventArgs e)
    {
        Log("正在从浏览器提取B站Cookie...");
        _ = Task.Run(() =>
        {
            var result = CookieExtractor.GetBilibiliCookies();
            Post(() => ApplyCookieResult(result, "bilibili"));
        });
    }

    private void OnFetchTiebaCookieClick(object sender, RoutedEventArgs e)
    {
        Log("正在从浏览器提取贴吧Cookie...");
        _ = Task.Run(() =>
        {
            var result = CookieExtractor.GetTiebaCookies();
            Post(() => ApplyCookieResult(result, "tieba"));
        });
    }

    private void ApplyCookieResult(CookieExtractionResult result, string platform)
    {
        var name = platform == "bilibili" ? "B站" : "贴吧";
        if (result.Success && result.Cookies is not null)
        {
            var cookies = result.Cookies;
            if (platform == "bilibili")
            {
                BiliSessBox.Text = Get(cookies, "SESSDATA");
                BiliJctBox.Text = Get(cookies, "bili_jct");
                BiliUidBox.Text = Get(cookies, "DedeUserID");
            }
            else
            {
                TiebaBdussBox.Text = Get(cookies, "BDUSS");
                TiebaStokenBox.Text = Get(cookies, "STOKEN");
            }
            var source = Get(cookies, "_source");
            Log($"{name}Cookie获取成功！（来源：{source}）", "success");
            ShowToast($"{name}Cookie 获取成功");
            return;
        }

        Log($"{name}Cookie 自动获取失败：{result.Reason}", "error");
        ShowTextViewer("无法自动获取 Cookie",
            $"{name} Cookie 自动获取失败：\n\n{result.Reason}\n\n" +
            "手动获取方法：浏览器登录后按 F12 → Application（应用程序）→ Cookies，" +
            "复制对应字段粘贴到设置页。\n" +
            "B站：SESSDATA / bili_jct / DedeUserID；贴吧：BDUSS / STOKEN。");

        static string Get(Dictionary<string, string> dict, string key) =>
            dict.TryGetValue(key, out var value) ? value : "";
    }

    private void OnOpenBilibiliLoginClick(object sender, RoutedEventArgs e) =>
        CookieExtractor.OpenBilibiliLogin();

    private void OnOpenTiebaLoginClick(object sender, RoutedEventArgs e) =>
        CookieExtractor.OpenTiebaLogin();

    private TiebaCollector MakeTiebaCollector(CancellationToken cancellationToken = default)
    {
        var (interval, jitter, cooldown) = _config.TiebaPacing();
        return new TiebaCollector(_config.TiebaCookieString,
            interval, jitter, cooldown,
            _config.TiebaSearchSort(), cancellationToken);
    }

    // ================================================================
    // 时效窗口
    // ================================================================

    private WindowSpec MainWindowSpec() => WindowSpec.Normalize(new WindowSpec
    {
        Years = WinYears.IntValue,
        Months = WinMonths.IntValue,
        Days = WinDays.IntValue,
        Hours = WinHours.IntValue,
    });

    private WindowSpec SettingsWindowSpec() => WindowSpec.Normalize(new WindowSpec
    {
        Years = SettingsWinYears.IntValue,
        Months = SettingsWinMonths.IntValue,
        Days = SettingsWinDays.IntValue,
        Hours = SettingsWinHours.IntValue,
    });

    private void ApplyWindowToMain(WindowSpec window)
    {
        WinYears.SetValueSilently(window.Years);
        WinMonths.SetValueSilently(window.Months);
        WinDays.SetValueSilently(window.Days);
        WinHours.SetValueSilently(window.Hours);
    }

    private void ApplyWindowToSettings(WindowSpec window)
    {
        SettingsWinYears.SetValueSilently(window.Years);
        SettingsWinMonths.SetValueSilently(window.Months);
        SettingsWinDays.SetValueSilently(window.Days);
        SettingsWinHours.SetValueSilently(window.Hours);
    }

    private void OnTimeWindowValueChanged(object sender, EventArgs e)
    {
        if (!_uiReady || _loadingConfig || _syncingTime) return;
        _syncingTime = true;
        ApplyWindowToSettings(MainWindowSpec());
        _syncingTime = false;
        UpdateWindowLabels();
    }

    private void OnSettingsTimeWindowValueChanged(object sender, EventArgs e)
    {
        if (!_uiReady || _loadingConfig || _syncingTime) return;
        _syncingTime = true;
        ApplyWindowToMain(SettingsWindowSpec());
        _syncingTime = false;
        UpdateWindowLabels();
    }

    private void OnQuickWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 4) return;
        int Parse(int index) => int.TryParse(parts[index], out var value) ? value : 0;
        var window = WindowSpec.Normalize(new WindowSpec
        {
            Years = Parse(0), Months = Parse(1), Days = Parse(2), Hours = Parse(3),
        });
        _syncingTime = true;
        ApplyWindowToMain(window);
        ApplyWindowToSettings(window);
        _syncingTime = false;
        UpdateWindowLabels();
    }

    private void UpdateWindowLabels()
    {
        var label = "当前：" + Relevance.WindowLabel(MainWindowSpec());
        TimeWindowLabel.Text = label;
        SettingsTimeWindowLabel.Text = label;
    }

    private void HookNumberSync(NumericBox first, NumericBox second)
    {
        first.ValueChanged += (_, _) =>
        {
            if (_loadingConfig || _syncingNumbers) return;
            _syncingNumbers = true;
            second.SetValueSilently(first.Value);
            _syncingNumbers = false;
        };
        second.ValueChanged += (_, _) =>
        {
            if (_loadingConfig || _syncingNumbers) return;
            _syncingNumbers = true;
            first.SetValueSilently(second.Value);
            _syncingNumbers = false;
        };
    }

    // ================================================================
    // 分析流程
    // ================================================================

    private async void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (_analyzing)
        {
            RequestCancel();
            return;
        }
        await StartAnalysisAsync().ConfigureAwait(true);
    }

    private async Task StartAnalysisAsync()
    {
        UpdateConfigFromUi();

        var mode = _mode;
        string keyword;
        string platform;
        string query;
        string videoInput = "";

        if (mode == "single_video")
        {
            videoInput = VideoBox.Text.Trim();
            if (videoInput.Length == 0)
            {
                Log("请输入 BV 号或视频 URL", "error");
                ShowToast("请输入 BV 号或视频 URL", "warning");
                return;
            }
            keyword = videoInput;
            platform = "";
            query = videoInput;
        }
        else
        {
            platform = PlatformBox.Text.Trim();
            keyword = KeywordBox.Text.Trim();
            if (keyword.Length == 0)
            {
                Log("请输入关键词（平台/吧名可选）", "error");
                ShowToast("请输入关键词", "warning");
                return;
            }
            query = Relevance.ComposeKeyword(platform, keyword);
        }

        if (!_config.IsLlmConfigured)
        {
            Log("请先配置 LLM API Key", "error");
            ShowToast("请先在设置页配置 LLM API Key", "warning");
            NavSettings.IsChecked = true;
            return;
        }

        var useBili = mode != "single_video" ? BiliCheck.IsChecked == true : true;
        var useTieba = mode == "keyword" && TiebaCheck.IsChecked == true;
        if (mode != "single_video" && !useBili && !useTieba)
        {
            Log("请至少勾选一个平台（B站 / 贴吧）", "error");
            ShowToast("请至少勾选一个平台", "warning");
            return;
        }
        var window = MainWindowSpec();
        var timeWindowHours = Relevance.WindowHours(window);
        var windowText = Relevance.WindowLabel(window);
        if (mode != "single_video" && useBili && !_config.IsBilibiliConfigured)
        {
            Log("B站未配置 Cookie，请在设置页填写", "warning");
        }
        if (mode == "keyword" && useTieba && !_config.IsTiebaConfigured)
        {
            Log("贴吧未配置 Cookie：新版贴吧接口支持匿名访问，仍可采集；填写 BDUSS 可降低风控概率",
                "warning");
        }
        if (mode == "keyword" && useTieba && platform.Length == 0)
        {
            Log("未填写「平台/吧名」：贴吧将退回全站搜索；填写吧名可先搜吧、再在吧内搜关键词",
                "warning");
        }

        _keyword = keyword;
        _platform = platform;
        _query = query;

        var biliCookie = useBili ? _config.BilibiliCookieString : "";
        var tiebaCookie = useTieba ? _config.TiebaCookieString : "";
        var pacing = _config.TiebaPacing();
        var searchSort = _config.TiebaSearchSort();
        var hotFirst = _config.TiebaHotFirst();
        var hotTopics = _config.TiebaHotTopics();
        var (deepBili, deepTieba) = _config.DeepCounts();
        var maxVariants = _config.MaxVariants();
        var biliPages = BiliPages.IntValue;
        var biliComments = BiliComments.IntValue;
        var tiebaPages = TiebaPages.IntValue;
        var tiebaReplies = TiebaReplies.IntValue;
        var apiBase = _config.GetString("llm", "api_base");
        var apiKey = _config.GetString("llm", "api_key");
        var model = _config.GetString("llm", "model");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _analyzing = true;
        SetAnalyzingUi(true);
        ClearDataInternal();
        SetStatus("● LLM 分析中...");
        ActivityStatusText.Text = "运行中";
        AddActivity("▶", $"[{ModeName(mode)}] {query}", ActivityLevel.Info);
        if (mode != "single_video")
        {
            AddActivity("◎", $"平台/吧名：{(platform.Length > 0 ? platform : "未指定")}｜" +
                              $"关键词：{keyword}｜时效窗口：{windowText}", ActivityLevel.Info);
        }
        Log($"{ModeName(mode)}开始：{query}");

        await Task.Run(async () =>
        {
            await RunAnalysisAsync(mode, keyword, platform, query, videoInput,
                window, useBili, useTieba, biliCookie, tiebaCookie, pacing, searchSort,
                hotFirst, hotTopics, deepBili, deepTieba, maxVariants,
                biliPages, biliComments, tiebaPages, tiebaReplies,
                apiBase, apiKey, model, token).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private string ModeName(string mode) => mode switch
    {
        "single_video" => "单视频深度分析",
        "bili_overview" => "B站相关视频概览",
        _ => "关键词综合分析",
    };

    private AnalyzeCallbacks BuildCallbacks()
    {
        return new AnalyzeCallbacks
        {
            OnToolCall = (name, args) =>
            {
                var desc = FormatToolCall(name, args);
                Post(() => AddActivity("🔍 调用工具", desc, ActivityLevel.Tool));
            },
            OnToolResult = (_, summary) =>
                Post(() => AddActivity("✓", summary, ActivityLevel.Result)),
            OnThinking = text =>
                Post(() => AddActivity("🤔", text, ActivityLevel.Think)),
            OnReportReset = () =>
            {
                _streamPump.Reset();
                Interlocked.Exchange(ref _streamResetPending, 1);
            },
            OnReportDelta = text => _streamPump.Append(text),
        };
    }

    private async Task RunAnalysisAsync(
        string mode, string keyword, string platform, string query, string videoInput,
        WindowSpec window, bool useBili, bool useTieba,
        string biliCookie, string tiebaCookie,
        (double Interval, double Jitter, double Cooldown) pacing, int searchSort,
        bool hotFirst, int hotTopics, int deepBili, int deepTieba, int maxVariants,
        int biliPages, int biliComments, int tiebaPages, int tiebaReplies,
        string apiBase, string apiKey, string model, CancellationToken token)
    {
        var callbacks = BuildCallbacks();
        LlmAnalyzer? analyzer = null;
        try
        {
            analyzer = new LlmAnalyzer(apiBase, apiKey, model, token);
            Dictionary<string, object?> result;
            try
            {
                result = mode switch
                {
                    "single_video" => await analyzer.AnalyzeSingleVideoAsync(
                        videoInput, biliCookie, 15, callbacks).ConfigureAwait(false),
                    "bili_overview" => await analyzer.AnalyzeBiliOverviewAsync(
                        keyword, biliCookie, platform,
                        Relevance.WindowHours(window), 20, callbacks).ConfigureAwait(false),
                    _ => await analyzer.AnalyzeAgenticAsync(
                        keyword, biliCookie, tiebaCookie, useBili, useTieba,
                        pacing, searchSort, platform,
                        Relevance.WindowHours(window), 15, hotFirst, hotTopics,
                        callbacks).ConfigureAwait(false),
                };
            }
            catch (FunctionCallingNotSupportedException ex)
            {
                Post(() =>
                {
                    AddActivity("⚠", "API不支持函数调用，回退到传统模式", ActivityLevel.Think);
                    Log("API不支持函数调用，使用传统模式: " + ex.Message, "warning");
                });

                Dictionary<string, object?>? biliData = null;
                Dictionary<string, object?>? tiebaData = null;
                var collectNote = "";
                if (mode == "keyword")
                {
                    using var bili = useBili ? new BilibiliCollector(biliCookie) : null;
                    using var tieba = useTieba
                        ? new TiebaCollector(tiebaCookie, pacing.Interval, pacing.Jitter,
                            pacing.Cooldown, searchSort, token)
                        : null;
                    var (b, t, report) = await Pipeline.CollectTwoStageAsync(
                        keyword, platform, bili, tieba, window, deepBili, deepTieba,
                        maxVariants,
                        text => Post(() => AddActivity("·", "  " + text, ActivityLevel.Info)),
                        cancelCheck: () => token.IsCancellationRequested,
                        hotFirst: hotFirst, hotTopics: hotTopics,
                        cancellationToken: token).ConfigureAwait(false);
                    biliData = b;
                    tiebaData = t;
                    collectNote = report.PromptNote();
                }
                else if (mode == "single_video")
                {
                    biliData = await CollectSingleVideoAsync(videoInput, biliCookie, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    using var bili = new BilibiliCollector(biliCookie);
                    biliData = await bili.CollectKeywordDataAsync(query, biliPages, biliComments,
                        token).ConfigureAwait(false);
                }

                Post(() => AddActivity("·", "采集完成，正在分析...", ActivityLevel.Info));
                var reportText = await analyzer.AnalyzeTraditionalAsync(
                    keyword, biliData, tiebaData, collectNote, callbacks).ConfigureAwait(false);
                result = new Dictionary<string, object?>
                {
                    ["report"] = reportText,
                    ["bilibili_data"] = biliData,
                    ["tieba_data"] = tiebaData,
                };
            }

            result["usage"] = new Dictionary<string, long>(analyzer.Usage);
            var finalResult = result;
            Post(() => CompleteAnalysis(finalResult, "complete", ""));
        }
        catch (OperationCanceledException)
        {
            Post(() => CompleteAnalysis(null, "cancelled", ""));
        }
        catch (CollectCancelledException)
        {
            Post(() => CompleteAnalysis(null, "cancelled", ""));
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                AddActivity("❌", "分析失败：" + ex.Message, ActivityLevel.Error);
                Log("分析出错: " + ex.Message, "error");
            });
            Post(() => CompleteAnalysis(null, "error", ex.Message));
        }
        finally
        {
            analyzer?.Dispose();
        }
    }

    private async Task<Dictionary<string, object?>?> CollectSingleVideoAsync(
        string videoInput, string cookie, CancellationToken token)
    {
        var bvid = BilibiliCollector.ResolveBvid(videoInput);
        if (bvid is null)
        {
            Log("无法解析BV号: " + videoInput, "error");
            return null;
        }
        using var collector = new BilibiliCollector(cookie);
        var info = await collector.GetVideoInfoAsync(bvid, token).ConfigureAwait(false);
        Post(() => AddActivity("✓",
            $"视频: 《{HttpUtils.Trim(Item.Str(info, "title"), 30)}》 播放:{Item.Long(info, "play")}",
            ActivityLevel.Result));
        var comments = new List<Dictionary<string, object?>>();
        for (var page = 1; page <= 3; page++)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            try
            {
                var batch = await collector.GetVideoCommentsAsync(
                    Item.Long(info, "aid"), page, 20, 0, token).ConfigureAwait(false);
                if (batch.Count == 0) break;
                comments.AddRange(batch);
                if (comments.Count >= 60) break;
            }
            catch
            {
                break;
            }
        }
        return new Dictionary<string, object?>
        {
            ["videos"] = new List<Dictionary<string, object?>> { info },
            ["comments"] = comments,
            ["video_info"] = info,
            ["total_videos"] = 1,
            ["total_comments"] = comments.Count,
        };
    }

    private void CompleteAnalysis(Dictionary<string, object?>? result, string status, string error)
    {
        if (Interlocked.Exchange(ref _streamResetPending, 0) == 1)
        {
            RawReport.Clear();
            _streamChars = 0;
        }
        _analyzing = false;
        SetAnalyzingUi(false);
        ActivityStatusText.Text = status switch
        {
            "complete" => "就绪",
            "cancelled" => "已停止",
            _ => "出错",
        };

        if (result is not null)
        {
            _biliData = result.GetValueOrDefault("bilibili_data") as Dictionary<string, object?>;
            _tiebaData = result.GetValueOrDefault("tieba_data") as Dictionary<string, object?>;
            PopulateData();
            var report = result.GetValueOrDefault("report")?.ToString() ?? "";
            _analysisReport = report;
            FinishStreaming(report);
            _lastUsage = result.GetValueOrDefault("usage") as Dictionary<string, long>;
            UpdateUsageLabels(_lastUsage);
            SetStatus("● 完成");
            AddActivity("✅", "分析完成！", ActivityLevel.Result);
            Log($"{ModeName(_mode)}完成", "success");
            _ = ArchiveAsync();
            ShowToast("分析完成，报告已生成");
        }
        else if (status == "cancelled")
        {
            var partial = _streamPump.Take();
            if (!string.IsNullOrEmpty(partial))
            {
                RawReport.AppendText(partial);
                _streamChars += partial.Length;
            }
            if (_analysisReport.Length == 0 && RawReport.Text.Length > 0)
            {
                _analysisReport = RawReport.Text;
            }
            ReportCharsText.Text = $"{RawReport.Text.Length} 字";
            SetStatus("● 已停止");
            AddActivity("⛔", "分析已被手动停止", ActivityLevel.Error);
            Log("分析已手动停止", "warning");
            ShowRawReport();
        }
        else
        {
            SetStatus("● 出错");
            ShowToast("分析失败，请查看运行日志", "error");
        }
    }

    private async Task ArchiveAsync()
    {
        var mode = ModeName(_mode);
        var query = _query;
        var report = _analysisReport;
        var bili = _biliData;
        var tieba = _tiebaData;
        var usage = _lastUsage;
        if (report.Length == 0) return;

        try
        {
            var path = await Task.Run(() =>
                HistoryStore.SaveAnalysis(mode, query, report, usage, bili, tieba))
                .ConfigureAwait(true);
            Log($"报告已归档: {path}", "info");
        }
        catch (Exception ex)
        {
            Log("归档失败: " + ex.Message, "warning");
        }
    }

    private void UpdateUsageLabels(Dictionary<string, long>? usage)
    {
        if (usage is null || !usage.TryGetValue("total_tokens", out var total) || total <= 0)
        {
            HeaderUsageText.Text = "";
            FooterUsageText.Text = "";
            UsageText.Text = "";
            return;
        }
        var text = $"本次 {total:N0} tokens · {usage["requests"]} 次请求";
        HeaderUsageText.Text = text;
        FooterUsageText.Text = text;
        UsageText.Text = $"输入 {usage["prompt_tokens"]:N0} / 输出 {usage["completion_tokens"]:N0}";
    }

    private void SetAnalyzingUi(bool analyzing)
    {
        if (analyzing)
        {
            AnalyzeButton.Content = "⛔  停止分析";
            AnalyzeButton.Background = ThemePalette.Error;
            AnalyzeButton.IsEnabled = true;
            AnalyzeButton.Foreground = Brushes.White;
        }
        else
        {
            AnalyzeButton.Content = "🚀  一键分析";
            AnalyzeButton.ClearValue(BackgroundProperty);
            AnalyzeButton.Foreground = Brushes.White;
            AnalyzeButton.IsEnabled = true;
        }
        AnalyzeButton.Width = 160;
    }

    private void RequestCancel()
    {
        if (!_analyzing) return;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }
        AnalyzeButton.Content = "正在停止...";
        AnalyzeButton.IsEnabled = false;
        SetStatus("● 正在停止...");
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_analyzing)
        {
            ShowToast("分析进行中，请先停止", "warning");
            return;
        }
        ClearDataInternal();
        SetStatus("● 就绪");
        ShowToast("数据已清空");
    }

    private void ClearDataInternal()
    {
        _biliData = null;
        _tiebaData = null;
        _analysisReport = "";
        _lastUsage = null;
        BiliVideoGrid.ItemsSource = null;
        BiliCommentGrid.ItemsSource = null;
        TiebaPostGrid.ItemsSource = null;
        TiebaReplyGrid.ItemsSource = null;
        ResetReportArea();
        UpdateUsageLabels(null);
    }

    private void ResetReportArea()
    {
        _renderVersion++;
        _streamPump.Reset();
        Interlocked.Exchange(ref _streamResetPending, 0);
        RawReport.Clear();
        ReportPreview.Document = null;
        ReportPreview.Visibility = Visibility.Collapsed;
        RawReport.Visibility = Visibility.Visible;
        _renderedText = "";
        _renderedBlocks = null;
        _renderedDocument = null;
        _streamChars = 0;
        ReportCharsText.Text = "0 字";
        RenderStatusText.Visibility = Visibility.Collapsed;
    }

    private void PopulateData()
    {
        if (_biliData is not null)
        {
            var videos = _biliData.GetValueOrDefault("videos") as List<Dictionary<string, object?>>
                         ?? new List<Dictionary<string, object?>>();
            BiliVideoGrid.ItemsSource = videos.Select(v => new BiliVideoRow
            {
                Title = Item.Str(v, "title"),
                Author = Item.Str(v, "author"),
                Play = Item.Long(v, "play"),
                Like = Item.Long(v, "like"),
                Reply = Item.Long(v, "reply"),
                Favorites = Item.Long(v, "favorites"),
                PubDate = Item.Str(v, "pubdate"),
                Url = Item.Str(v, "url"),
            }).ToList();

            var comments = _biliData.GetValueOrDefault("comments") as List<Dictionary<string, object?>>
                           ?? new List<Dictionary<string, object?>>();
            BiliCommentGrid.ItemsSource = comments.Select(c => new BiliCommentRow
            {
                User = Item.Str(c, "user"),
                Content = Item.Str(c, "content"),
                Like = Item.Long(c, "like"),
                Time = Item.Str(c, "ctime"),
            }).ToList();
        }

        if (_tiebaData is not null)
        {
            var posts = _tiebaData.GetValueOrDefault("posts") as List<Dictionary<string, object?>>
                        ?? new List<Dictionary<string, object?>>();
            TiebaPostGrid.ItemsSource = posts.Select(p => new TiebaPostRow
            {
                Title = Item.Str(p, "title"),
                Bar = Item.Str(p, "bar"),
                Author = Item.Str(p, "author"),
                ReplyCount = Item.Long(p, "reply_count"),
                Date = Item.Str(p, "date"),
                Url = Item.Str(p, "url"),
            }).ToList();

            var replies = _tiebaData.GetValueOrDefault("replies") as List<Dictionary<string, object?>>
                          ?? new List<Dictionary<string, object?>>();
            TiebaReplyGrid.ItemsSource = replies.Select(r => new TiebaReplyRow
            {
                User = Item.Str(r, "user"),
                Content = Item.Str(r, "content"),
                Floor = Item.Long(r, "floor"),
                Time = Item.Str(r, "time"),
            }).ToList();
        }
    }

    // ================================================================
    // 报告显示 / 渲染（防卡顿核心）
    // ================================================================

    private void OnReportReset()
    {
        _streamPump.Reset();
        Interlocked.Exchange(ref _streamResetPending, 1);
    }

    private void OnStreamTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _streamResetPending, 0) == 1)
        {
            _renderVersion++;
            RawReport.Clear();
            RawReport.Visibility = Visibility.Visible;
            ReportPreview.Visibility = Visibility.Collapsed;
            ReportPreview.Document = null;
            _renderedText = "";
            _renderedBlocks = null;
            _renderedDocument = null;
            _streamChars = 0;
            ReportCharsText.Text = "0 字";
            RenderStatusText.Visibility = Visibility.Collapsed;
        }

        var delta = _streamPump.Take();
        if (delta.Length == 0) return;

        RawReport.AppendText(delta);
        _streamChars += delta.Length;
        ReportCharsText.Text = $"{_streamChars:N0} 字";
        if (_rawAtBottom) RawReport.ScrollToEnd();
    }

    private void FinishStreaming(string report)
    {
        if (Interlocked.Exchange(ref _streamResetPending, 0) == 1)
        {
            RawReport.Clear();
            _streamChars = 0;
        }
        var tail = _streamPump.Take();
        if (tail.Length > 0)
        {
            RawReport.AppendText(tail);
            _streamChars += tail.Length;
        }
        if (RawReport.Text != report)
        {
            RawReport.Text = report;
            _streamChars = report.Length;
        }
        ReportCharsText.Text = $"{_streamChars:N0} 字";
        if (RenderPreviewCheck.IsChecked == true)
        {
            _ = RenderReportAsync();
        }
        else
        {
            ShowRawReport();
        }
    }

    private void OnRenderPreviewChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (RenderPreviewCheck.IsChecked == true)
        {
            if (_analysisReport.Length > 0) _ = RenderReportAsync();
        }
        else
        {
            _renderVersion++;
            ShowRawReport();
        }
    }

    private void ShowRawReport()
    {
        ReportPreview.Visibility = Visibility.Collapsed;
        RawReport.Visibility = Visibility.Visible;
        RenderStatusText.Visibility = Visibility.Collapsed;
    }

    private void ShowPreviewReport(FlowDocument document)
    {
        ReportPreview.Document = document;
        ReportPreview.Visibility = Visibility.Visible;
        RawReport.Visibility = Visibility.Collapsed;
    }

    private async Task RenderReportAsync()
    {
        if (RenderPreviewCheck.IsChecked != true) return;
        var text = _analysisReport;
        if (string.IsNullOrEmpty(text)) return;

        if (_renderedText == text && _renderedDocument is not null)
        {
            ShowPreviewReport(_renderedDocument);
            return;
        }

        var version = ++_renderVersion;
        RenderStatusText.Visibility = Visibility.Visible;
        var blocks = _renderedBlocks;
        if (blocks is null || _renderedText != text)
        {
            blocks = await Task.Run(() => MarkdownParser.Parse(text)).ConfigureAwait(true);
        }
        if (version != _renderVersion) return;

        var document = MarkdownRenderer.CreateDocument();
        ReportPreview.Document = document;
        var index = 0;
        var stopwatch = Stopwatch.StartNew();
        while (index < blocks.Count)
        {
            while (index < blocks.Count && stopwatch.ElapsedMilliseconds < 8)
            {
                MarkdownRenderer.AppendBlock(document.Blocks, blocks[index++]);
            }
            if (version != _renderVersion) return;
            if (index < blocks.Count)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                stopwatch.Restart();
            }
        }
        if (version != _renderVersion) return;

        _renderedText = text;
        _renderedBlocks = blocks;
        _renderedDocument = document;
        RenderStatusText.Visibility = Visibility.Collapsed;
        ShowPreviewReport(document);
    }

    private void OnRawReportLoaded(object sender, RoutedEventArgs e)
    {
        _rawScroll = FindDescendant<ScrollViewer>(RawReport);
        if (_rawScroll is not null)
        {
            _rawScroll.ScrollChanged += (_, args) =>
            {
                if (args.VerticalChange == 0 && args.ExtentHeightChange == 0) return;
                _rawAtBottom = _rawScroll.ScrollableHeight <= 0 ||
                               _rawScroll.VerticalOffset >= _rawScroll.ScrollableHeight - 8;
            };
        }
    }

    private void OnRawReportScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_rawScroll is null) return;
        _rawAtBottom = _rawScroll.ScrollableHeight <= 0 ||
                       _rawScroll.VerticalOffset >= _rawScroll.ScrollableHeight - 8;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        var text = _analysisReport.Length > 0 ? _analysisReport : RawReport.Text;
        if (string.IsNullOrEmpty(text))
        {
            ShowToast("当前没有报告可复制", "warning");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            ShowToast("报告已复制到剪贴板");
        }
        catch
        {
            ShowToast("复制失败：剪贴板被占用", "error");
        }
    }

    // ================================================================
    // 导出 / 历史
    // ================================================================

    private void OnExportReportClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_analysisReport) && RawReport.Text.Length == 0)
        {
            ShowToast("暂无分析报告", "warning");
            return;
        }
        var label = _query.Length > 0 ? _query : _keyword;
        var dialog = new SaveFileDialog
        {
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"声呐报告_{SafeName(label)}_{DateTime.Now:yyyyMMdd_HHmmss}.md",
        };
        if (dialog.ShowDialog(this) != true) return;
        var body = "# " + label + " 舆情分析报告\n\n" +
                   $"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n---\n\n" +
                   (_analysisReport.Length > 0 ? _analysisReport : RawReport.Text);
        File.WriteAllText(dialog.FileName, body, new UTF8Encoding(false));
        Log("报告已保存: " + dialog.FileName, "success");
        ShowToast("报告已导出");
    }

    private void OnExportDataClick(object sender, RoutedEventArgs e)
    {
        if (_biliData is null && _tiebaData is null)
        {
            ShowToast("暂无采集数据", "warning");
            return;
        }
        var label = _query.Length > 0 ? _query : _keyword;
        var dialog = new SaveFileDialog
        {
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"声呐数据_{SafeName(label)}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        var data = new Dictionary<string, object?>
        {
            ["keyword"] = _keyword,
            ["platform"] = _platform,
            ["query"] = label,
            ["time_window"] = MainWindowSpec().ToJson(),
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["bilibili"] = _biliData,
            ["tieba"] = _tiebaData,
        };
        File.WriteAllText(dialog.FileName, JsonUtil.Serialize(data, indented: true),
            new UTF8Encoding(false));
        Log("数据已保存: " + dialog.FileName, "success");
        ShowToast("数据已导出");
    }

    private void OnOpenHistoryFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            HistoryStore.OpenFolder();
        }
        catch (Exception ex)
        {
            ShowToast("打开历史目录失败: " + ex.Message, "error");
        }
    }

    private void RefreshHistory()
    {
        var entries = HistoryStore.ListEntries();
        HistoryList.ItemsSource = entries;
        if (entries.Count > 0)
        {
            HistoryEmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            HistoryEmptyText.Visibility = Visibility.Visible;
            HistoryEmptyText.Text = "暂无历史归档，完成一次分析后会自动保存";
        }
    }

    private void OnRefreshHistoryClick(object sender, RoutedEventArgs e) => RefreshHistory();

    private async void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;
        var version = ++_historyVersion;
        var text = await Task.Run(() => HistoryStore.LoadReport(entry.Path)).ConfigureAwait(true);
        if (version != _historyVersion) return;
        HistoryEmptyText.Visibility = Visibility.Collapsed;

        var blocks = await Task.Run(() => MarkdownParser.Parse(text)).ConfigureAwait(true);
        if (version != _historyVersion) return;
        var document = MarkdownRenderer.CreateDocument();
        HistoryPreview.Document = document;
        var index = 0;
        var stopwatch = Stopwatch.StartNew();
        while (index < blocks.Count)
        {
            while (index < blocks.Count && stopwatch.ElapsedMilliseconds < 8)
            {
                MarkdownRenderer.AppendBlock(document.Blocks, blocks[index++]);
            }
            if (version != _historyVersion) return;
            if (index < blocks.Count)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                stopwatch.Restart();
            }
        }
    }

    private static string SafeName(string? name)
    {
        var cleaned = new string((name ?? "").Select(c =>
            "\\/:*?\"<>|".Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()).Trim('_');
        return cleaned.Length == 0 ? "report" : cleaned[..Math.Min(cleaned.Length, 30)];
    }

    private void OnBiliVideoDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BiliVideoGrid.SelectedItem is BiliVideoRow { Url.Length: > 0 } row) OpenUrl(row.Url);
    }

    private void OnTiebaPostDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TiebaPostGrid.SelectedItem is TiebaPostRow { Url.Length: > 0 } row) OpenUrl(row.Url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    // ================================================================
    // 日志 / 活动 / 状态 / Toast
    // ================================================================

    private void Log(string message, string level = "info")
    {
        var prefix = level switch
        {
            "success" => "✓ ",
            "warning" => "! ",
            "error" => "✕ ",
            _ => "· ",
        };
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}\n");
        if (LogBox.Text.Length > 80000)
        {
            LogBox.Text = LogBox.Text[^60000..];
        }
        LogBox.ScrollToEnd();
    }

    private void AddActivity(string prefix, string text, ActivityLevel level)
    {
        var (brush, textBrush) = level switch
        {
            ActivityLevel.Tool => ((Brush)FindResource("AccentHoverBrush"), (Brush)FindResource("TextBrush")),
            ActivityLevel.Result => ((Brush)FindResource("SuccessBrush"), (Brush)FindResource("TextBrush")),
            ActivityLevel.Think => ((Brush)FindResource("WarningBrush"), (Brush)FindResource("TextDimBrush")),
            ActivityLevel.Error => ((Brush)FindResource("ErrorBrush"), (Brush)FindResource("ErrorBrush")),
            _ => ((Brush)FindResource("TextMutedBrush"), (Brush)FindResource("TextDimBrush")),
        };
        var item = new ActivityItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Prefix = prefix,
            Text = text,
            Brush = brush,
            TextBrush = textBrush,
        };
        _activities.Add(item);
        while (_activities.Count > 600) _activities.RemoveAt(0);
        ActivityList.ScrollIntoView(item);
    }

    private void OnClearActivityClick(object sender, RoutedEventArgs e) => _activities.Clear();

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusDot.Fill = text.Contains("失败", StringComparison.Ordinal) ||
                         text.Contains("出错", StringComparison.Ordinal)
            ? (Brush)FindResource("ErrorBrush")
            : text.Contains("停止", StringComparison.Ordinal)
                ? (Brush)FindResource("WarningBrush")
                : (Brush)FindResource("SuccessBrush");
    }

    private void ShowToast(string message, string level = "success")
    {
        var version = ++_toastVersion;
        ToastText.Text = message;
        ToastHost.BorderBrush = level switch
        {
            "error" => (Brush)FindResource("ErrorBrush"),
            "warning" => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("AccentBorderBrush"),
        };
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _ = HideToastAsync(version);
    }

    private async Task HideToastAsync(int version)
    {
        await Task.Delay(2200).ConfigureAwait(true);
        if (version != _toastVersion) return;
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        ToastHost.BeginAnimation(OpacityProperty, animation);
        await Task.Delay(240).ConfigureAwait(true);
        if (version == _toastVersion) ToastHost.Visibility = Visibility.Collapsed;
    }

    private void ShowTextViewer(string title, string body)
    {
        var window = new TextViewerWindow(title, body, this);
        window.ShowDialog();
    }

    private void Post(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private string FormatToolCall(string name, System.Text.Json.Nodes.JsonObject args)
    {
        string S(string key) => args[key]?.ToString() ?? "";
        int I(string key, int fallback) =>
            int.TryParse(args[key]?.ToString(), out var value) ? value : fallback;
        return name switch
        {
            "search_bilibili" => $"搜索B站「{S("keyword")}」(第{I("page", 1)}页)",
            "get_bilibili_comments" =>
                $"获取B站评论(av{S("aid")}, 第{I("page", 1)}页, 限{I("limit", 20)}条)",
            "get_video_info" => $"获取视频详情({S("bvid")})",
            "search_tieba_forums" => $"搜索贴吧吧名「{S("word")}」",
            "search_tieba" => $"搜索贴吧「{S("keyword")}」(第{I("page", 1)}页" +
                               (string.IsNullOrWhiteSpace(S("forum")) ? "，全站" : $"，吧内：{S("forum")}") + ")",
            "get_tieba_replies" => $"获取贴吧回复(帖子{S("post_id")})",
            _ => $"{name}({args.ToJsonString()})",
        };
    }

    // ================================================================
    // 贴吧诊断
    // ================================================================

    private void OnDiagnoseTiebaClick(object sender, RoutedEventArgs e)
    {
        UpdateConfigFromUi();
        Log("正在诊断贴吧采集通路（搜索 / 吧列表 / 帖子楼层 / 热点榜单）...");
        _ = Task.Run(async () =>
        {
            var lines = new List<string>();
            var okCount = 0;

            var control = await HttpUtils.DiagnoseAsync(HttpUtils.DiagnoseControlUrl)
                .ConfigureAwait(false);
            var networkOk = control.Any(entry => entry.Ok);
            foreach (var entry in control)
            {
                lines.Add($"百度（对照）{entry.Route}：" + (entry.Ok
                    ? $"HTTP {entry.Status}"
                    : $"请求失败 {entry.Detail}"));
            }

            using var collector = MakeTiebaCollector();
            lines.Add($"当前请求节奏：{collector.PacingSummary()}，风控冷却 {TiebaCollector.FmtSeconds(collector.BlockCooldown)} 秒");

            try
            {
                var posts = await collector.SearchPostsAsync("测试", 1).ConfigureAwait(false);
                okCount++;
                lines.Add($"✅ 全吧搜索（移动端接口，走线 {collector.RequestRoute}）：{posts.Count} 条结果");
                if (posts.Count > 0)
                {
                    lines.Add($"     例：{HttpUtils.Trim(Item.Str(posts[0], "title"), 30)}" +
                              $"（{Item.Str(posts[0], "bar")}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"❌ 全吧搜索失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}");
            }

            var tid = "";
            try
            {
                var threads = await collector.GetBarPostsAsync("天堂鸡汤", 1).ConfigureAwait(false);
                okCount++;
                lines.Add($"✅ 吧帖子列表（App protobuf 接口）：{threads.Count} 条");
                if (threads.Count > 0)
                {
                    tid = Item.Str(threads[0], "id");
                    lines.Add($"     例：{HttpUtils.Trim(Item.Str(threads[0], "title"), 30)}" +
                              $"（回复 {Item.Long(threads[0], "reply_count")}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"❌ 吧帖子列表失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}");
            }

            if (tid.Length > 0)
            {
                try
                {
                    var detail = await collector.GetPostRepliesAsync(tid, 1).ConfigureAwait(false);
                    var replies = detail["replies"] as List<Dictionary<string, object?>>
                                  ?? new List<Dictionary<string, object?>>();
                    okCount++;
                    lines.Add($"✅ 帖子楼层（App protobuf 接口）：标题《{HttpUtils.Trim(detail["title"]?.ToString() ?? "", 20)}》，" +
                              $"{replies.Count} 条回复");
                }
                catch (Exception ex)
                {
                    lines.Add($"❌ 帖子楼层失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}");
                }
            }

            try
            {
                var topics = await collector.FetchHotTopicsAsync().ConfigureAwait(false);
                okCount++;
                lines.Add($"✅ 贴吧热榜（热议榜话题接口）：{topics.Count} 个话题");
                if (topics.Count > 0)
                {
                    var sample = topics[0];
                    lines.Add($"     例：第 {Item.Long(sample, "rank")} 名「{Item.Str(sample, "name")}」");
                    try
                    {
                        var hotPosts = await collector.FetchHotTopicPostsAsync(sample).ConfigureAwait(false);
                        lines.Add($"     话题页帖子：{hotPosts.Count} 条" +
                                  (hotPosts.Count > 0
                                      ? $"，例《{HttpUtils.Trim(Item.Str(hotPosts[0], "title"), 20)}》"
                                      : ""));
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"     ⚠ 话题页解析失败：{ex.GetType().Name}: " +
                                  HttpUtils.Trim(ex.Message.Split('\n')[0], 80));
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"❌ 贴吧热榜失败：{ex.GetType().Name}: {HttpUtils.Trim(ex.Message.Split('\n')[0], 90)}");
            }

            string conclusion;
            if (okCount == 4)
            {
                conclusion = "✅ 结论：贴吧采集各条通路（搜索 / 吧列表 / 帖子楼层 / 热点榜单）全部正常，可以正常分析贴吧数据。";
            }
            else if (okCount > 0)
            {
                conclusion = "⚠️ 结论：贴吧部分通路可用。若某项失败且提示「安全验证」，多为临时风控，稍等几分钟重试即可。";
            }
            else if (networkOk)
            {
                conclusion = "⚠️ 结论：本机网络正常（百度可访问），但贴吧接口全部失败。\n" +
                             "若提示「百度安全验证」：属贴吧侧的访问限制，可换网络出口（手机热点 / 代理）后重试；\n" +
                             "若提示网络/代理错误：请检查代理设置。";
            }
            else
            {
                conclusion = "⚠️ 结论：连百度都无法访问，是网络连接问题（检查网络/代理），不是贴吧或 Cookie 问题。";
            }

            var summary = string.Join("\n", lines) + "\n\n" + conclusion;
            Post(() =>
            {
                Log(summary.Replace("\n", " "), "info");
                ShowTextViewer("贴吧连接诊断", summary);
            });
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _streamTimer.Stop();
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // 忽略
        }
        base.OnClosed(e);
    }
}
