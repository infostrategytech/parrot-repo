using Parrot.Web.Features.Dashboard.Tools;
using Parrot.Web.Features.Settings;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Opening hours as Google Business Profile has them (designer, 7 November 2026): more hours after a break, open
// 24 hours, special days, and temporarily closed.
public sealed class BusinessHoursTests
{
    private static readonly TimeOnly Nine = new(9, 0), One = new(13, 0), Two = new(14, 0), Six = new(18, 0);

    [Fact]
    public void ADayCanHaveMoreHoursAfterABreak()
    {
        var day = new DayHours(DayOfWeek.Monday, true, Nine, One, More: [new TimeRange(Two, Six)]);

        Assert.Equal([new TimeRange(Nine, One), new TimeRange(Two, Six)], day.Ranges);
        Assert.Null(day.TimesProblem("Monday"));
    }

    [Fact]
    public void HoursAfterABreakMustStartAfterTheFirstEnd()
    {
        var day = new DayHours(DayOfWeek.Monday, true, Nine, Two, More: [new TimeRange(One, Six)]);

        Assert.Equal("Monday's hours overlap. Start each after the one before ends.", day.TimesProblem("Monday"));
    }

    [Fact]
    public void AnAllDayDayHasNoTimesToGetWrong()
    {
        var day = new DayHours(DayOfWeek.Friday, true, Six, Nine, AllDay: true);

        Assert.Null(day.TimesProblem("Friday"));
        Assert.Single(day.Ranges);
    }

    [Fact]
    public void SpecialDaysAreCheckedOnSave()
    {
        var christmas = new DateOnly(2026, 12, 25);
        var details = BusinessDetails.Defaults;

        Assert.Equal("25 December closes before it opens. Check its hours.",
            (details with { SpecialDays = [new SpecialDay(christmas, true, Six, Nine)] }).Problem);
        Assert.Equal("25 December is listed twice. Remove one.",
            (details with { SpecialDays = [new SpecialDay(christmas, false, Nine, Six), new SpecialDay(christmas, true, Nine, Six)] }).Problem);
        Assert.Null((details with { SpecialDays = [new SpecialDay(christmas, false, Six, Nine)] }).Problem);
    }

    [Fact]
    public void TheBookingToolDescribesBreaksAndAllDayHours()
    {
        DayHours Day(DayOfWeek day, bool open = true) => new(day, open, Nine, Six);
        var hours = new[]
        {
            Day(DayOfWeek.Monday) with { Closes = One, More = [new TimeRange(Two, Six)] },
            Day(DayOfWeek.Tuesday) with { Closes = One, More = [new TimeRange(Two, Six)] },
            Day(DayOfWeek.Wednesday), Day(DayOfWeek.Thursday), Day(DayOfWeek.Friday),
            Day(DayOfWeek.Saturday) with { AllDay = true }, Day(DayOfWeek.Sunday, open: false),
        };

        var text = BookingFormat.OpeningHours(hours);

        Assert.NotNull(text);
        Assert.Contains("1pm, 2pm", text);
        Assert.Contains("open 24 hours", text);
    }
}
