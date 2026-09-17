using Microsoft.EntityFrameworkCore;
using Parrot.Application.Models;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class WhatsAppConnectionTests
{
    [Theory]
    [InlineData(false, false, false, "awaiting-webhook-verification")]
    [InlineData(false, false, true, "awaiting-webhook-verification")]
    [InlineData(false, true, false, "awaiting-test-message")]
    [InlineData(false, true, true, "receiving-configured")]
    [InlineData(true, false, false, "disabled")]
    [InlineData(true, false, true, "disabled")]
    [InlineData(true, true, false, "disabled")]
    [InlineData(true, true, true, "disabled")]
    public void SetupStatus_UsesTimestampPrecedence(bool disabled, bool verified, bool received, string expected)
    {
        var connection = new WhatsAppConnection
        {
            OwnerUserId = "owner",
            Name = "Customer Support",
            VerifyTokenEncrypted = "encrypted-token",
            DisabledAt = disabled ? DateTime.UtcNow : null,
            WebhookVerifiedAt = verified ? DateTime.UtcNow : null,
            FirstMessageReceivedAt = received ? DateTime.UtcNow : null,
        };

        Assert.Equal(expected, connection.SetupStatus);
    }

    [Fact]
    public void Model_EnforcesOwnershipAndActivePhoneUniquenessWithoutStoringStatus()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=model_test;Username=test;Password=test")
            .Options);
        var entity = context.Model.FindEntityType(typeof(WhatsAppConnection))!;

        Assert.Equal("WhatsAppConnections", entity.GetTableName());
        Assert.Null(entity.FindProperty(nameof(WhatsAppConnection.SetupStatus)));
        var owner = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(ApplicationUser), owner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, owner.DeleteBehavior);
        Assert.Equal(nameof(WhatsAppConnection.OwnerUserId), Assert.Single(owner.Properties).Name);
        var phoneIndex = Assert.Single(entity.GetIndexes(), i => i.Properties.Single().Name == nameof(WhatsAppConnection.PhoneNumberId));
        Assert.True(phoneIndex.IsUnique);
        Assert.Equal("\"PhoneNumberId\" IS NOT NULL AND \"DisabledAt\" IS NULL", phoneIndex.GetFilter());
        Assert.True(Assert.Single(entity.GetIndexes(), i => i.Properties.Single().Name == nameof(WhatsAppConnection.WebhookIdentifier)).IsUnique);
        Assert.False(entity.FindProperty(nameof(WhatsAppConnection.UpdatedAt))!.IsNullable);
        Assert.Equal("timestamp with time zone", entity.FindProperty(nameof(WhatsAppConnection.FirstMessageReceivedAt))!.GetColumnType());
    }
}
