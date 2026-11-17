# 日历与待办

用于 Windows 桌面的浮动日历与待办清单。可以手动添加日程，也可以从个人 163 邮箱识别面试、笔试和测评截止时间。绑定邮箱是可选的。

## 下载和安装

适用 Windows 10 22H2 x64 和 Windows 11 x64。本版本在 Windows 10 22H2 x64 验证。安装包随附 .NET 和 Windows App SDK 运行组件，无需另外安装开发工具或运行时。

1. 打开 [GitHub Releases](https://github.com/Stella8392/CalendarTodo/releases/latest)，在 Assets 中下载 Windows x64 安装 ZIP。
2. 解压整个 ZIP。保留 MSI 和 CAB 在同一目录，阅读随包提供的第三方许可，然后双击 MSI。安装按当前 Windows 用户进行。
3. 从开始菜单打开「日历与待办」。升级前，从小组件右上角点「×」退出正在运行的版本。

安装包采用自包含部署，当前发布未做 Authenticode 签名。可将下载文件的 SHA-256 与同一 Release 中的校验文件对照。 [Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

## 桌面操作

| 要做什么 | 操作 |
| --- | --- |
| 移动小组件 | 按住顶部标题区，拖到目标位置后松手 |
| 改变窗口大小 | 拖动窗口任一边或角 |
| 改变日程区高度 | 上下拖动日程区和今日待办之间的横条 |
| 固定窗口或收起 | 点「置顶」或「隐藏」。隐藏后可从任务栏通知区域唤回 |
| 浏览日程 | 点日历日期查看当天日程。日期右上角的红圈数字是当天的日程数 |
| 添加日程 | 点「＋ 日程」。选择日期和时间，默认是当前北京时间 |
| 管理待办 | 在底部输入任务并选择优先级。勾选后划线、置底，取消勾选可恢复 |
| 删除日程或待办 | 右键单击对应条目，选择「删除」并确认。删除日程也会移除关联待办 |
| 打开邮箱网页 | 点「打开邮箱 ▾」。在「管理邮箱链接」中可添加、编辑或删除网页入口 |

「今日待办」只展示北京时间当天的任务与日程待办。添加到未来日期的日程，会在相应日期自动进入当天的待办。窗口位置、大小、日程区高度和置顶状态会保存。

## 可选的 163 邮箱同步

1. 登录 [163 邮箱网页版](https://mail.163.com/)，打开「设置 → POP3/SMTP/IMAP」。开启 IMAP，并按页面提示生成客户端授权码。
2. 在小组件的「设置」中填写自己的个人 163 邮箱地址和**客户端授权码**，点击「测试并绑定」。
3. 点「同步」进行首次导入。首次检查最近 30 天的收件箱，之后从上次保存的同步位置增量检查。
4. 在「待确认」中核对识别不完整的邮件，可补充时间、选择要更新的日程或忽略候选。

程序运行时，默认每天北京时间 20:00 自动同步。可在「设置 → 每日同步时间」调整，点击「保存设置」应用。当天设定时间以后启动时，会补跑当天尚未完成的一轮。「隐藏」保留后台同步与提醒，「×」退出程序。日程可在开始或截止前 24 小时与 30 分钟通过 Windows 通知提醒。 [网易邮箱授权码指引](https://help.mail.126.com/faqDetail.do?code=d7a5dc8471cd0c0e8b4b8f4f8e49998b374173cfe9171305fa1a7089)

## 提供反馈

点「同步」右侧的「反馈」，确认后在浏览器打开 GitHub Issue 表单。登录 GitHub、检查内容并提交后，反馈会记录在仓库的 Issues 页面，公开可见。请避免填写邮箱、授权码、私人日程或含个人信息的截图。应用只预填产品版本和反馈提示。 [查看反馈](https://github.com/Stella8392/CalendarTodo/issues)

## 本地数据与隐私

- 无需注册本项目的账号。应用没有项目服务端或应用遥测。邮件同步由本机通过 TLS 直接只读访问 163 收件箱，邮件在本机解析。点击邮箱网页入口时，由系统默认浏览器打开对应站点。
- Windows 凭据管理器保存客户端授权码。SQLite 保存待办、日程、同步位置、部分邮件元数据及待确认所需的短片段。数据库是当前 Windows 用户的本地文件，包含个人数据，请避免把它上传或分享。
- 数据默认在当前用户的 %LOCALAPPDATA%\CalendarTodo\Data。需要自定义位置时，可在启动程序的进程环境中设置 CALENDARTODO_DATA_DIR 为自己的可写路径。
- 解除绑定会移除该绑定的 Windows 凭据。卸载前可先解除绑定。程序卸载会保留本地数据库，若要同时移除本地记录，请在退出程序后删除自己的数据目录。
- Windows 及运行时组件的数据处理受其各自的隐私政策约束。 [Microsoft 隐私声明](https://privacy.microsoft.com/privacystatement)

## 从源码构建

需要 Windows x64、.NET 10 SDK 和 WiX Toolset 5。构建只从仓库源码与 NuGet 依赖生成程序，不使用任何邮箱账号或本地数据。

~~~powershell
dotnet tool install --tool-path .\dist\tools wix --version 5.0.2
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet run --project Verification/CalendarTodo.Verification.csproj
.\Installer\Build.ps1 -Wix .\dist\tools\wix.exe
~~~

构建结果默认输出到 dist 目录，可用 OutputDirectory 参数改目录。安装或使用时请阅读并遵守随附的第三方条款。 [第三方组件与许可](THIRD-PARTY-NOTICES.md)
