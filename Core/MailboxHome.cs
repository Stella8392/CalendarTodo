namespace CalendarTodo;

public sealed record MailboxHome(string Name, string Address)
{
    public static bool TryCreate(string name, string address, out MailboxHome? mailbox)
    {
        mailbox = null;
        name = name.Trim();
        if (name.Length is 0 or > 40 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            return false;
        mailbox = new MailboxHome(name, uri.AbsoluteUri);
        return true;
    }
}
