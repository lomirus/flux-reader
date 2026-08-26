# FluxReader

FluxReader 是一个只面向 Windows 11 的本地 RSS/Atom 阅读器。界面采用 WinUI 3 三栏布局，交互参考 NetNewsWire，但实现保持 Windows 原生：Mica 背景、系统主题、原生对话框、系统浏览器跳转和 Windows 应用通知。

## 当前功能

- 添加、移除和刷新 RSS 2.0、RSS 1.0/RDF、Atom 订阅
- 所有文章、未读文章、收藏文章和按订阅源浏览
- 阅读状态、收藏状态与文章正文的本地持久化
- ETag / Last-Modified 条件请求，避免重复下载
- 应用运行期间每 15 分钟自动刷新；发现新文章时发送 Windows 系统通知
- 跟随系统、亮色、暗色三种主题
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

- `reader.db`：订阅、文章、已读与收藏状态
- `settings.json`：主题等轻量设置

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
- 暂未包含 OPML 导入/导出、网站订阅自动发现、文件夹管理、搜索和跨设备同步。
