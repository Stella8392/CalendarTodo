# 日历与待办

用于 Windows 桌面的浮动日历与待办清单。可以手动添加日程，也可以同时连接多个 163、QQ 或 Gmail 邮箱，统一识别面试、笔试和测评截止时间。绑定邮箱是可选的。

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

## 多邮箱授权与同步

支持同时绑定多个个人邮箱，包括 163（@163.com）、QQ（@qq.com、@foxmail.com）和 Gmail（@gmail.com、@googlemail.com）。每个账号使用自己的授权凭据，分别保存增量同步进度，日程统一进入日历和今日待办。悬停日程标题可查看来源邮箱。「待确认」也显示来源邮箱。

### 在插件中绑定

1. 点小组件的「设置」，在「邮箱渠道」中选择服务商。页面会显示该渠道的授权步骤和官方帮助入口。
2. 按下方教程准备好自己的授权码或应用专用密码。
3. 在「邮箱地址」中填写自己的完整邮箱账号（包含 @ 和邮箱后缀），再填写授权码或应用专用密码，点击「测试并添加 / 更新」。连接测试成功后才加入绑定列表。
4. 可以继续添加其他邮箱。关闭设置后点击「同步」，会依次同步所有已绑定账号。首次分别检查各账号最近 30 天的收件箱，只导入尚未开始或截止的日程，后续按各自 UID 增量读取。
5. 已绑定列表显示每个账号的最近同步状态。「更新授权」可重新填写该账号的授权凭据。「解除绑定」后再点「确认解除」，会移除该账号的本地凭据并停止同步，已导入的日程保留。

### 「邮箱地址」填写完整邮箱账号

**「邮箱与设置」中的「邮箱地址」一栏填写你收发邮件使用的完整邮箱账号。QQ 邮箱应填写「你的 QQ 号@qq.com」，包含 @qq.com 后缀。**

| 邮箱渠道 | 邮箱账号示例格式 |
| --- | --- |
| QQ 邮箱 | 你的QQ号@qq.com，例如 123456789@qq.com |
| Foxmail 地址 | yourname@foxmail.com |
| 163 邮箱 | yourname@163.com |
| Gmail | yourname@gmail.com |

上表均为格式示例，请换成自己的邮箱账号。此处请勿填写 https://mail.qq.com/、https://mail.163.com/、https://mail.google.com/ 等网页地址，也请勿只填 QQ 号码而省略邮箱后缀。

「客户端授权码」一栏填写对应邮箱生成的客户端授权码。选择 Gmail 时，「Google 应用专用密码」一栏填写 Google 生成的应用专用密码。邮箱网页链接可以在「打开邮箱 ▾ → 管理邮箱链接」中填写。

### 163 邮箱授权

1. 登录 [163 邮箱网页版](https://mail.163.com/)。
2. 打开设置中的「POP3/SMTP/IMAP」，开启 IMAP 服务。
3. 按网易页面提示完成身份验证，生成客户端授权码。
4. 在插件中选择「163 邮箱」，填写自己的完整 @163.com 地址，将刚生成的授权码填入「客户端授权码」。

IMAP 连接使用 imap.163.com，端口 993，TLS。 [网易官方帮助](https://help.mail.163.com/)

### QQ 邮箱和 Foxmail 地址授权

先查看 QQ 邮箱官方的授权码说明，再按自己的网页版提示操作。 [QQ 邮箱官方授权码说明与接入入口](https://wx.mail.qq.com/list/readtemplate?name=app_intro.html#/agreement/authorizationCode)

1. 登录 QQ 邮箱网页版，进入「设置 → 账号与安全 → 安全设置」。如果已在「账号与安全」页面，直接选择左侧「安全设置」。 [打开 QQ 邮箱](https://mail.qq.com/)
2. 向下找到「POP3/IMAP/SMTP/Exchange/CardDAV 服务」区域，按页面提示开启包含 IMAP 的第三方客户端服务。不同版本也可能显示「POP3/IMAP/SMTP」或「第三方客户端管理」。
3. 按腾讯页面提示完成身份验证并获取客户端授权码。具体按钮和验证方式以当前页面及官方授权码说明为准。
4. 回到插件，在「邮箱渠道」中选择「QQ 邮箱」，在「邮箱地址」中填写自己的完整邮箱账号（例如 你的QQ号@qq.com），在「客户端授权码」中填写刚生成的授权码，点击「测试并添加 / 更新」。Foxmail 地址应使用该邮箱服务认可的完整 @foxmail.com 登录地址。

IMAP 连接使用 imap.qq.com，端口 993，TLS。该服务器信息用于插件连接，请在「邮箱地址」中填写自己的完整邮箱账号。 [QQ 邮箱官方授权码说明](https://wx.mail.qq.com/list/readtemplate?name=app_intro.html#/agreement/authorizationCode) · [QQ 邮箱帮助中心](https://service.mail.qq.com/)

### Gmail 应用专用密码

1. 登录自己的 Google 账号，进入「安全性」，开启「两步验证」。
2. 打开 [Google 应用专用密码](https://myaccount.google.com/apppasswords)，按提示重新验证身份，创建名为 CalendarTodo 的应用专用密码。
3. 复制 Google 生成的 16 位应用专用密码。在插件中选择「Gmail」，填写完整 @gmail.com 或 @googlemail.com 地址，将它填入「Google 应用专用密码」。粘贴时可保留 Google 显示的分组空格，程序会自动去除。
4. 个人 Gmail 的 IMAP 访问已默认开启。点击「测试并添加 / 更新」，成功后即可同步。

此版本的 Gmail 授权方式为应用专用密码。Google 提供该功能需要账号已开启两步验证。仅使用安全密钥、加入高级保护计划，或某些工作及学校账号可能没有应用专用密码选项。请依照 Google 官方说明检查账号条件。当前渠道支持上表列出的个人邮箱域名。 [Google 官方授权说明](https://support.google.com/accounts/answer/185833?hl=zh-Hans) · [Gmail 客户端说明](https://support.google.com/mail/answer/7126229?hl=zh-Hans)

IMAP 连接使用 imap.gmail.com，端口 993，TLS。电脑需要能直接连接该服务器，浏览器能打开 Gmail 与桌面应用能连接其 IMAP 端口是两项条件。 [Google IMAP 文档](https://developers.google.com/workspace/gmail/imap/imap-smtp)

### 同步、提醒与故障处理

程序运行时，默认每天北京时间 20:00 自动同步。可在「设置 → 每日同步时间」调整并保存。当日设定时间以后启动，会补跑各账号当天尚未完成的一轮。单个邮箱失败后会继续同步其他邮箱，成功账号当天自动同步去重，网络失败的账号等待重试。授权失效的账号会提示更新授权，仍可手动重试。「隐藏」保留后台同步与提醒，「×」退出程序。

- 「授权失效」时，检查完整邮箱地址、所选渠道、IMAP 开启状态，并按渠道教程重新生成授权码或应用专用密码，在该账号的「更新授权」中保存。
- 「连接失败」或「连接超时」时，检查本机网络和对应 IMAP 服务的连接情况。Gmail 还需检查本机是否能连接 imap.gmail.com 的 993 端口。
- 识别不完整的邮件进入「待确认」，可补充时间、选择同一邮箱下的原日程或忽略。
- 不同邮箱收到同一份邀请时，各邮箱分别保留来源和记录。可按来源邮箱查看或删除重复日程。
- 日程可在开始或截止前 24 小时与 30 分钟通过 Windows 通知提醒。

从 1.2.0 升级时，原来的 163 绑定会自动加入多账号列表，原有授权凭据、日程和同步进度继续使用。

## 提供反馈

点「同步」右侧的「反馈」，确认后在浏览器打开 GitHub Issue 表单。登录 GitHub、检查内容并提交后，反馈会记录在仓库的 Issues 页面，公开可见。请避免填写邮箱、授权码、私人日程或含个人信息的截图。应用只预填产品版本和反馈提示。 [查看反馈](https://github.com/Stella8392/CalendarTodo/issues)

## 本地数据与隐私

- 无需注册本项目的账号。应用没有项目服务端或应用遥测。邮件同步由本机通过 TLS 直接只读访问对应服务商的收件箱，邮件在本机解析。点击邮箱网页入口时，由系统默认浏览器打开对应站点。
- Windows 凭据管理器保存客户端授权码。SQLite 保存待办、日程、同步位置、部分邮件元数据及待确认所需的短片段。数据库是当前 Windows 用户的本地文件，包含个人数据，请避免把它上传或分享。
- 数据默认在当前用户的 %LOCALAPPDATA%\CalendarTodo\Data。需要自定义位置时，可在启动程序的进程环境中设置 CALENDARTODO_DATA_DIR 为自己的可写路径。
- 每个账号的授权凭据独立保存。解除绑定会移除该账号的 Windows 凭据，卸载前可分别解除绑定。程序卸载会保留本地数据库，若要同时移除本地记录，请在退出程序后删除自己的数据目录。
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
