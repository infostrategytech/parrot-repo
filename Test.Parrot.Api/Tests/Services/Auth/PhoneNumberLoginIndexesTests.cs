using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Application.Models;
using Parrot.Infrastructure.Data;

namespace Test.Parrot.Api.Tests.Services.Auth;

public class PhoneNumberLoginIndexesTests
{
    [Fact]
    public void Model_KeepsTheNationalHalfUniqueAndTheSnapshotInStep()
    {
        using var context = NewContext();

        Assert.False(context.Database.HasPendingModelChanges());

        var index = Assert.Single(
            context.Model.FindEntityType(typeof(ApplicationUser))!.GetIndexes(),
            i => i.Properties.Single().Name == nameof(ApplicationUser.PhoneNumber));

        Assert.True(index.IsUnique);
        Assert.Equal("\"PhoneNumber\" IS NOT NULL", index.GetFilter());
    }

    // Sign-in also compares against the two columns joined, which no column
    // holds — so that index lives in the migration as SQL rather than in the
    // model. What the model cannot see, the generated script must still carry,
    // in both directions.
    [Fact]
    public void Migration_ReplacesTheIndexOnThePairWithTheIndexOnEachHalf()
    {
        using var context = NewContext();
        IMigrator migrator = context.GetService<IMigrator>();

        string forward = migrator.GenerateScript(
            "20260928215252_AddUserProfileSupport", "20260929131142_AddPhoneNumberLoginIndexes");

        Assert.Contains("DROP INDEX \"IX_AspNetUsers_PhoneDialCode_PhoneNumber\"", forward);
        Assert.DoesNotContain("CREATE UNIQUE INDEX \"IX_AspNetUsers_PhoneDialCode_PhoneNumber\"", forward);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_AspNetUsers_FullNumber\"", forward);
        Assert.Contains("(\"PhoneDialCode\" || \"PhoneNumber\")", forward);

        string backward = migrator.GenerateScript(
            "20260929131142_AddPhoneNumberLoginIndexes", "20260928215252_AddUserProfileSupport");

        Assert.Contains("DROP INDEX \"IX_AspNetUsers_FullNumber\"", backward);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_AspNetUsers_PhoneDialCode_PhoneNumber\"", backward);
    }

    private static ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=model_test;Username=test;Password=test").Options);
}
