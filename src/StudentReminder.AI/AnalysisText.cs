using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StudentReminder.AI;

public static partial class AnalysisText
{
    public static string Prepare(string text)
    {
        string sanitized;
        try { sanitized = Sensitive().Replace(text, "[已隐藏]"); }
        catch (RegexMatchTimeoutException) { return "[内容已隐藏]"; }
        var length = Math.Min(sanitized.Length, Core.AiLimits.MaxTextLength);
        if (length < sanitized.Length && char.IsHighSurrogate(sanitized[length - 1])) length--;
        return sanitized[..length];
    }

    public static bool IsRelevant(string text) => Terms().IsMatch(text) ||
        (Timing().IsMatch(text) && Action().IsMatch(text));

    public static bool HasProtectedSignal(string text) => IsRelevant(text) || ProtectedTime().IsMatch(text);
    public static bool IsContextFollowUp(string text) => FollowUp().IsMatch(text);
    public static bool IsImportantNotice(string text) => ImportantNotice().IsMatch(text);

    [GeneratedRegex("重要|务必|必须|全体|所有人|统一|必填|逾期|上交|提交|填报|登记表", RegexOptions.CultureInvariant)]
    private static partial Regex ImportantNotice();

    [GeneratedRegex(@"补充|还是|改到|改成|延期|取消|照旧|按刚才|^\s*[0-9]{1,2}(?:[点时]|[:：][0-5][0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex FollowUp();

    [GeneratedRegex(@"\d{1,2}(?:[点时]|[:：][0-5]\d)|\d{4}-\d{1,2}-\d{1,2}|\d{1,2}月\d{1,2}[日号]|(?:周|星期)[一二三四五六日天]|今天|明天|后天|今晚|改到|取消|延后|提前|照旧|顺延|推迟", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTime();

    [GeneratedRegex(@"https?://[^\s<>，。；！、]+|(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|(?<![A-Za-z0-9_])(?:wxid_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]{8,})(?![A-Za-z0-9_])|(?<![0-9])(?:[1-9][0-9]{16}[0-9Xx]|(?:\+?86[ -]?)?1[3-9][0-9]{9})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Sensitive();

    [GeneratedRegex("作业|补测|考试|测验|报告|提交|截止|报名|预约|抢票|抢名额|候补|缴费|开会|会议|讲座|报告会|学术报告|签到|竞赛|班会|改期|延期|取消|通知|公告|公示|通告|活动|问卷|填报|信息采集|登记表|打印|上交|材料收集", RegexOptions.CultureInvariant)]
    private static partial Regex Terms();

    [GeneratedRegex(@"明天|后天|今晚|周[一二三四五六日天]|[0-9]{1,2}(?:点|月|:[0-9]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();

    [GeneratedRegex("交|到|带|参加|集合|完成|召开|领取|上传|填写|准备|记得", RegexOptions.CultureInvariant)]
    private static partial Regex Action();
}

public static partial class EventTimeParser
{
    private const string DatePattern = @"(?:(?<year>[0-9]{4})(?:\s*年\s*(?<month>[0-9]{1,2})\s*月\s*(?<day>[0-9]{1,2})\s*[日号]|(?<separator>[-./])(?<month>[0-9]{1,2})\k<separator>(?<day>[0-9]{1,2}))|(?<shortMonth>[0-9]{1,2})(?:\s*月\s*(?<shortDay>[0-9]{1,2})\s*[日号]|[./](?<shortDay>[0-9]{1,2}))|(?<week>本|这|下下|下)?(?:周|星期)(?<weekday>[一二三四五六日天])|(?<relative>今天|今日|今晚|明天|后天))(?:\s*\(\s*(?:周|星期)(?<calendarWeekday>[一二三四五六日天])\s*\))?";

    public static DateTimeOffset? Parse(string excerpt, DateTimeOffset sentAt, TimeZoneInfo zone)
    {
        var text = excerpt.Normalize(NormalizationForm.FormKC).Trim();
        var match = Expression().Match(text);
        if (!match.Success) return null;
        try { return ParseMatch(match, sentAt, zone); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static DateOnly? ParseDate(string excerpt, DateTimeOffset sentAt, TimeZoneInfo zone)
    {
        var match = DateExpression().Match(excerpt.Normalize(NormalizationForm.FormKC).Trim());
        if (!match.Success) return null;
        try
        {
            var reference = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sentAt, zone).DateTime);
            var date = ResolveDate(match, reference);
            if (!CalendarWeekdayMatches(match, date)) return null;
            if ((match.Groups["shortMonth"].Success || (match.Groups["weekday"].Success && !match.Groups["week"].Success)) &&
                date < reference) return null;
            return date;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset? ParseMatch(Match match, DateTimeOffset sentAt, TimeZoneInfo zone)
    {
        var explicitOffset = ParseOffset(match.Groups["offset"].Value);
        if (match.Groups["offset"].Success && explicitOffset is null) return null;
        var reference = explicitOffset is { } offset ? sentAt.ToOffset(offset) : TimeZoneInfo.ConvertTime(sentAt, zone);
        var date = ResolveDate(match, DateOnly.FromDateTime(reference.DateTime));
        if (!CalendarWeekdayMatches(match, date)) return null;
        var hour = Number(match, "hour");
        var minute = match.Groups["half"].Success ? 30 : Number(match, "minute");
        var second = Number(match, "second");
        var part = match.Groups["part"].Value;
        if (part is "下午" or "晚上" or "傍晚" || match.Groups["relative"].Value == "今晚")
        {
            if (hour is >= 1 and < 12) hour += 12;
        }
        if (part == "凌晨" && hour == 12) hour = 0;
        if (hour > 23 || minute > 59 || second > 59 || (part is "早上" or "上午" or "凌晨" && hour > 12)) return null;

        var wallTime = date.ToDateTime(new TimeOnly(hour, minute, second), DateTimeKind.Unspecified);
        if (explicitOffset is null && (zone.IsInvalidTime(wallTime) || zone.IsAmbiguousTime(wallTime))) return null;
        var resolved = new DateTimeOffset(wallTime, explicitOffset ?? zone.GetUtcOffset(wallTime));
        // A short date or bare weekday can otherwise silently resolve to the wrong year or week.
        if ((match.Groups["shortMonth"].Success || (match.Groups["weekday"].Success && !match.Groups["week"].Success)) &&
            resolved < sentAt) return null;
        return resolved;
    }

    private static bool CalendarWeekdayMatches(Match match, DateOnly date)
    {
        var named = match.Groups["calendarWeekday"];
        return !named.Success ||
            ((int)date.DayOfWeek + 6) % 7 == "一二三四五六日".IndexOf(named.Value.Replace('天', '日'), StringComparison.Ordinal);
    }

    private static DateOnly ResolveDate(Match match, DateOnly reference)
    {
        if (match.Groups["year"].Success)
            return new DateOnly(Number(match, "year"), Number(match, "month"), Number(match, "day"));
        if (match.Groups["shortMonth"].Success)
            return new DateOnly(reference.Year, Number(match, "shortMonth"), Number(match, "shortDay"));
        if (match.Groups["weekday"].Success)
        {
            var monday = reference.AddDays(-(((int)reference.DayOfWeek + 6) % 7));
            var week = match.Groups["week"].Value switch { "下" => 7, "下下" => 14, _ => 0 };
            var weekday = match.Groups["weekday"].Value.Replace('天', '日');
            return monday.AddDays(week + "一二三四五六日".IndexOf(weekday, StringComparison.Ordinal));
        }
        return reference.AddDays(match.Groups["relative"].Value switch { "明天" => 1, "后天" => 2, _ => 0 });
    }

    private static int Number(Match match, string name) =>
        match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;

    private static TimeSpan? ParseOffset(string text)
    {
        if (text is "Z" or "UTC") return TimeSpan.Zero;
        if (text == "北京时间") return TimeSpan.FromHours(8);
        if (text.StartsWith("UTC", StringComparison.Ordinal)) text = text[3..];
        if (text.Length < 2) return null;
        var fields = text[1..].Split(':');
        var hours = int.Parse(fields[0], CultureInfo.InvariantCulture);
        var minutes = fields.Length == 2 ? int.Parse(fields[1], CultureInfo.InvariantCulture) : 0;
        return hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)
            ? null : TimeSpan.FromMinutes((text[0] == '-' ? -1 : 1) * (hours * 60 + minutes));
    }

    [GeneratedRegex(@"\A" + DatePattern + @"(?:\s*(?:凌晨|早上|上午|中午|下午|傍晚|晚上))?\s*(?:之前|以前|前|截止)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateExpression();

    [GeneratedRegex(@"\A" + DatePattern + @"[T ]*(?<part>凌晨|早上|上午|中午|下午|傍晚|晚上)?\s*(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2})(?::(?<second>[0-9]{2}))?|[点时](?:(?<half>半)|(?<minute>[0-9]{1,2})分?)?)\s*(?<offset>Z|北京时间|UTC(?:[+-][0-9]{1,2}(?::[0-9]{2})?)?|[+-][0-9]{2}:[0-9]{2})?\s*(?:之前|以前|前|截止)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Expression();
}
