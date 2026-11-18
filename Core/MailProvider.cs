using MimeKit;

namespace CalendarTodo;

public sealed record MailProvider(string Id, string Name, string Host, string Homepage,
    string HelpUrl, string Instructions, params string[] Domains)
{
    public static IReadOnlyList<MailProvider> All { get; } =
    [
        new("163", "163 邮箱", "imap.163.com", "https://mail.163.com/",
            "https://help.mail.163.com/",
            "登录 163 网页版，在设置的 POP3/SMTP/IMAP 中开启 IMAP，完成验证并生成客户端授权码。", "163.com"),
        new("qq", "QQ 邮箱", "imap.qq.com", "https://mail.qq.com/",
            "https://service.mail.qq.com/",
            "登录 QQ 邮箱网页版，在设置中找到 POP3/IMAP/SMTP 或第三方客户端管理，开启 IMAP/SMTP 服务，完成验证后生成授权码。Foxmail 地址也可选择 QQ 邮箱。", "qq.com", "foxmail.com"),
        new("gmail", "Gmail", "imap.gmail.com", "https://mail.google.com/",
            "https://support.google.com/accounts/answer/185833?hl=zh-Hans",
            "先在 Google 账号的安全设置开启两步验证，再到 myaccount.google.com/apppasswords 创建 CalendarTodo 应用专用密码，填入生成的 16 位密码。个人 Gmail 的 IMAP 默认开启。组织账号或高级保护账号可能无法创建应用专用密码。电脑需能连接 Gmail 的 IMAP 服务。", "gmail.com", "googlemail.com")
    ];

    public static MailProvider? ForAddress(string address)
    {
        address = address.Trim();
        if (!MailboxAddress.TryParse(address, out var mailbox) ||
            !string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
            return null;
        var domain = address[(address.LastIndexOf('@') + 1)..];
        return All.FirstOrDefault(provider => provider.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));
    }

    public string NormalizeSecret(string secret) => Id == "gmail" ?
        string.Concat(secret.Where(character => !char.IsWhiteSpace(character))) : secret.Trim();
}
