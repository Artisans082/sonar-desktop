# 声呐 Sonar 

> LLM 驱动的 B站 & 贴吧舆情分析工具，基于原 Python 项目重构的 Windows 桌面版。
>
>
> 原项目：[bzhant/sonar](https://github.com/bzhant/sonar) ｜ 当前版本：**v0.3.0**
>


---


## 核心改进

相较于 Python 版，C# 版主要做了三方面优化：

| # | 方向 | 说明 |
|---|------|------|
| 1 | **贴吧访问** | 改用 aiotieba-go 同款移动端 / App 接口，解决贴吧 Cookie 失效、网页端被安全验证拦截时的访问问题 |
| 2 | **搜索质量** | 优化贴吧搜索：以**相关度**为主，辅以热度、讨论量与时效综合排序，减少泛词和旧帖干扰 |
| 3 | **整体流畅度** | 流式报告改为 40ms 合流刷新；Markdown 后台解析 + 8ms 分帧渲染；完成状态先反馈、归档异步执行 |


## 运行与构建

### 环境要求

| 场景 | 要求 |
|------|------|
| 运行发布包 | Windows 10 / 11，[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 从源码构建 | Windows 10 / 11，[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

> 请从源码构建运行，或从 [Releases](https://github.com/Artisans082/sonar-desktop/releases) 下载已打包的单文件程序。

### 从源码运行（推荐）

```powershell
# 克隆
git clone https://github.com/Artisans082/sonar-desktop.git
cd sonar-desktop

# 构建
dotnet build Sonar.slnx -c Release

# 运行
dotnet run --project Sonar.Desktop\Sonar.Desktop.csproj
```

### 打包单文件 exe

```powershell
dotnet publish Sonar.Desktop\Sonar.Desktop.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

生成的 `dist\Sonar.exe` 约 1.2 MB，为框架依赖版本，双击即可启动；
若提示缺少运行时，请先安装 .NET 10 Desktop Runtime。

### 从 Releases 下载

前往 [Releases](https://github.com/Artisans082/sonar-desktop/releases) 下载 `Sonar.exe`，双击运行即可，无需构建。
若 Releases 中暂无可用版本，请按上方「从源码运行」操作。

## 首次使用

1. 打开 **设置** → 选择 LLM 预设（DeepSeek / 通义千问 / OpenAI / Moonshot），填入 API Key；
2. 按需获取 B站 / 贴吧 Cookie（「一键获取」或手动粘贴）；
3. 回到 **搜索分析**，填写「平台/吧名 + 关键词」，点击 **一键分析**。

> 配置默认写入 `~/.sonar/config.json`，历史归档位于 `~/.sonar/history/`。

## 项目结构

```text
sonar0.3/
├── Sonar.Desktop/
│   ├── App.xaml / App.xaml.cs        # 应用入口
│   ├── MainWindow.xaml / .cs         # 红黑主界面（主页 / 设置 / 历史）
│   ├── AppPaths.cs / JsonUtil.cs / Item.cs
│   ├── Models/                       # 时效窗口、Markdown 块模型、表格行模型
│   ├── Services/
│   │   ├── ConfigManager.cs          # 配置持久化与旧配置兼容
│   │   ├── HttpUtils.cs              # 直连 / 代理、编码嗅探
│   │   ├── BilibiliCollector.cs      # WBI、搜索、评论、视频详情
│   │   ├── TiebaCollector.cs         # 移动端 / App protobuf / 热榜 / 楼层
│   │   ├── ProtobufMini.cs           # varint / length-delimited
│   │   ├── Relevance.cs              # 关键词拆分、相关性、时效、排序
│   │   ├── Pipeline.cs               # 两阶段采集编排
│   │   ├── LlmAnalyzer.cs            # SSE、Function Calling、三种分析模式
│   │   ├── MarkdownParser.cs         # Markdown → 块模型
│   │   ├── CookieExtractor.cs        # 多浏览器 Cookie 提取
│   │   └── HistoryStore.cs           # 历史归档
│   ├── Interop/WinSqlite.cs          # winsqlite3.dll P/Invoke
│   ├── Rendering/                    # 主题色与 FlowDocument 渲染
│   ├── Controls/NumericBox.xaml/.cs  # 数字输入框
│   ├── Themes/                       # 主题与控件样式
│   ├── Views/TextViewerWindow.xaml/.cs
│   └── Assets/                       # 应用图标
├── Sonar.slnx
├── README.md
└── LICENSE
```

## 已知限制

| 情况 | 表现 | 处理 |
|------|------|------|
| Chrome / Edge 正在运行 | Cookie 数据库被独占锁定 | 完全退出浏览器后重试 |
| Chrome 127+（v20 加密） | 只能由浏览器自身进程解密 | 按程序提示手动粘贴 Cookie |
| 浏览器 Cookie 隐私 | 仅读取当前用户自己的资料 | 不写入、不上传 |

> 本软件仅支持 Windows 10 / 11。

## 参考与致谢

- **原项目**：[bzhant/sonar](https://github.com/bzhant/sonar)。本项目在其基础上重构，沿用整体架构与基础实现，特此致谢。
- **贴吧协议参考**：[aiotieba](https://github.com/lumina37/aiotieba)（Python）、[aiotieba-go](https://github.com/rongyuio/aiotieba-go)（Go）。本项目未复制其源代码，仅参考移动端 / App 接口协议自行实现。
- WBI 签名算法参考社区公开的逆向资料。

## 许可

与原项目一致：[MIT](LICENSE)。
