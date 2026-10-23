using System.Windows.Media;

namespace Sonar.Models;

public sealed class BiliVideoRow
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public long Play { get; init; }
    public long Like { get; init; }
    public long Reply { get; init; }
    public long Favorites { get; init; }
    public string PubDate { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class BiliCommentRow
{
    public string User { get; init; } = "";
    public string Content { get; init; } = "";
    public long Like { get; init; }
    public string Time { get; init; } = "";
}

public sealed class TiebaPostRow
{
    public string Title { get; init; } = "";
    public string Bar { get; init; } = "";
    public string Author { get; init; } = "";
    public long ReplyCount { get; init; }
    public string Date { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class TiebaReplyRow
{
    public string User { get; init; } = "";
    public string Content { get; init; } = "";
    public long Floor { get; init; }
    public string Time { get; init; } = "";
}

public sealed class ActivityItem
{
    public string Time { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.White;
    public Brush TextBrush { get; init; } = Brushes.White;
}
