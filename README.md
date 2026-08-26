# FluxReader

FluxReader 是一个只面向 Windows 11 的本地 RSS/Atom 阅读器。界面采用 WinUI 3 三栏布局，交互参考 NetNewsWire，但实现保持 Windows 原生：Mica 背景、系统主题、原生对话框、系统浏览器跳转和 Windows 应用通知。

## 当前功能

- 添加、移除和刷新 RSS 2.0、RSS 1.0/RDF、Atom 订阅
- 使用可选的一级分组整理订阅；支持新建、重命名、移除分组和调整订阅所属分组
- 所有文章、未读文章、按订阅源浏览，以及筛选单个订阅源的未读文章
- 阅读状态与文章正文的本地持久化
- ETag / Last-Modified 条件请求，避免重复下载
- 显示 RSS/Atom 声明的订阅源图标，并在未声明时回退到网站 favicon
- 应用运行期间每 15 分钟自动刷新；发现新文章时发送 Windows 系统通知
- 独立设置页，支持跟随系统、亮色、暗色三种主题，以及简体中文、繁体中文、英语、法语、德语、意大利语、西班牙语（西班牙/拉丁美洲）、葡萄牙语（巴西）、波兰语、俄语、日语和韩语；首次启动自动匹配系统语言，不支持时回退英语
- 可拖拽调整订阅栏与文章列表宽度，自动记忆布局；双击分隔线可恢复默认值
- 点击通知激活应用，点击文章可用系统默认浏览器打开原文
- XML DTD 禁用、文档大小限制和纯文本正文转换

## 技术栈

- C# / .NET 10
- WinUI 3 / Windows App SDK 2.4.0
- CommunityToolkit.Mvvm 8.4.2
- Microsoft.Data.Sqlite 10.0.11（SQLite WAL）
- MSTest 4.3.3 + Microsoft.Testing.Platform
- `.slnx` 解决方案与 NuGet Central Package Management

项目目标框架为 `net10.0-windows10.0.26100.0`，最低平台版本为 `10.0.22000.0`，因此不会为 Windows 10 或旧设备保留适配代码。

## 项目结构

```text
src/
  FluxReader.Core/       RSS/Atom 解析与正文清理，无 UI 依赖
  FluxReader/            WinUI 3 应用、SQLite、刷新、通知与 MVVM
tests/
  FluxReader.Core.Tests/ 解析器单元测试
```

应用数据保存在 `%LOCALAPPDATA%\FluxReader`：

- `reader.db`：订阅、文章与已读状态
- `settings.json`：主题等轻量设置
- `notifications.log`：Windows 系统通知注册、发送或注销失败的诊断信息（仅在失败时创建）

品牌图标的可编辑矢量源文件位于 `assets/brand/fluxreader-icon.svg`；由它生成的 Windows 多尺寸图标位于 `assets/brand/fluxreader-icon.ico`。

## 构建与运行

前置条件：Windows 11、.NET 10 SDK、Windows SDK 10.0.26100，以及 Windows App SDK 2.4 Runtime。Visual Studio 用户可安装 WinUI 应用开发工作负载。

```powershell
dotnet restore FluxReader.slnx --configfile NuGet.Config
dotnet build FluxReader.slnx --configuration Release --no-restore
dotnet test tests\FluxReader.Core.Tests\FluxReader.Core.Tests.csproj --no-restore
dotnet run --project src\FluxReader\FluxReader.csproj
```

当前采用 Windows App SDK 官方支持的未打包桌面模式，启动时通过 `AppNotificationManager.Register()` 注册通知，不依赖手工 COM 或 AUMID 配置。发布到 Microsoft Store 时可在此基础上增加 MSIX 打包项目，而不需要改动阅读、存储或刷新层。

## 初版边界

- 自动刷新仅在应用运行期间执行；关闭应用后的后台刷新需要后续增加 MSIX 后台任务。
- 正文以安全的本地纯文本方式阅读，不执行订阅中的 HTML、脚本或第三方嵌入内容。
- 暂未包含 OPML 导入/导出、网站订阅自动发现、嵌套分组、搜索和跨设备同步。
