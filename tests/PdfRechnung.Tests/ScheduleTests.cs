using PdfRechnung.Delivery;
using Xunit;

namespace PdfRechnung.Tests;

public class ScheduleTests
{
    private static DateTime D(int day, int h, int m) => new(2026, 11, day, h, m, 0); // 16.11.2026 = Montag

    [Fact]
    public void Intervall_mit_Zeitfenster_und_Wochentagen()
    {
        var s = new Schedule { Mode = "intervall", Minutes = 5, From = "07:00", To = "18:00", Days = "Mo-Fr" };
        Assert.True(ScheduleEngine.Due(s, null, D(16, 7, 0)));
        Assert.False(ScheduleEngine.Due(s, D(16, 7, 0), D(16, 7, 3)));
        Assert.True(ScheduleEngine.Due(s, D(16, 7, 0), D(16, 7, 5)));
        Assert.False(ScheduleEngine.Due(s, null, D(16, 18, 0)));
        Assert.False(ScheduleEngine.Due(s, null, D(16, 6, 59)));
        Assert.False(ScheduleEngine.Due(s, null, D(21, 10, 0))); // Samstag
        Assert.Equal(D(17, 7, 0), ScheduleEngine.Next(s, D(16, 17, 58), D(16, 18, 1)));
        var night = new Schedule { Mode = "intervall", Minutes = 30, From = "22:00", To = "06:00" };
        Assert.True(ScheduleEngine.Due(night, null, D(16, 23, 0)));
        Assert.True(ScheduleEngine.Due(night, null, D(17, 2, 0)));
        Assert.False(ScheduleEngine.Due(night, null, D(17, 12, 0)));
    }

    [Fact]
    public void Feste_Uhrzeiten_dreimal_taeglich()
    {
        var s = new Schedule { Mode = "zeiten", Times = "07:00; 12:00; 17:30" };
        Assert.False(ScheduleEngine.Due(s, D(16, 7, 1), D(16, 11, 59)));
        Assert.True(ScheduleEngine.Due(s, D(16, 7, 1), D(16, 12, 0)));
        Assert.False(ScheduleEngine.Due(s, D(16, 12, 0), D(16, 12, 15)));
        Assert.True(ScheduleEngine.Due(s, D(16, 12, 0), D(17, 6, 0)));   // 17:30 verpasst (Dienst aus) → nachholen
        Assert.Equal(D(16, 17, 30), ScheduleEngine.Next(s, D(16, 12, 0), D(16, 12, 1)));
        Assert.Equal("um 07:00, 12:00, 17:30", ScheduleEngine.Describe(s));
    }

    [Fact]
    public void Sofort_und_aus()
    {
        Assert.True(ScheduleEngine.Due(new Schedule { Mode = "sofort" }, D(16, 7, 0), D(16, 7, 0).AddSeconds(15)));
        Assert.False(ScheduleEngine.Due(new Schedule { Mode = "aus" }, null, D(16, 7, 0)));
        Assert.Equal(new HashSet<int> { 5, 6, 7, 1 }, ScheduleEngine.ParseDays("Fr-Mo"));
    }

    [Fact]
    public void Kunde_uebersteuert_global_und_Ausgang_folgt_Abruf()
    {
        var g = new ChannelSettings { Type = "ftp", InboundInterval = 10, InboundSchedule = new Schedule { Mode = "zeiten", Times = "06:00" } };
        var own = new ChannelSettings { Type = "ftp" };
        var m = ChannelSettings.Merge(g, own);
        Assert.Equal("zeiten", m.InboundSchedule.Mode);
        Assert.Equal("zeiten", m.OutboxSchedule.Mode);
        own.OutboxSchedule = new Schedule { Mode = "sofort" };
        Assert.Equal("sofort", ChannelSettings.Merge(g, own).OutboxSchedule.Mode);
        Assert.Equal("intervall", ChannelSettings.Merge(null, new ChannelSettings { Type = "ftp", InboundInterval = 7 }).InboundSchedule.Mode);
    }
}
