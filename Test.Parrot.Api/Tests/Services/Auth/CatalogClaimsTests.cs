using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Email;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Models;
using Parrot.Application.Utils;

namespace Test.Parrot.Api.Tests.Services.Auth;

// The catalog service trusts these two claims for tenant isolation and authorisation.
public class CatalogClaimsTests
{
    [Fact]
    public void Issued_tokens_carry_tenant_id_and_catalog_scopes()
    {
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid().ToString(), Email = "owner@example.com", UserName = "owner@example.com", BusinessName = "Acme" };
        IEnumerable<Claim>? captured = null;
        Mock<IJwtHelper> jwt = new Mock<IJwtHelper>();
        jwt.Setup(j => j.GetDefaultExpiration()).Returns(DateTime.UtcNow.AddHours(1));
        jwt.Setup(j => j.GenerateToken(It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTime?>()))
            .Callback<IEnumerable<Claim>, DateTime?>((c, _) => captured = c.ToList())
            .Returns("token");

        object service = CreateService(jwt.Object);
        MethodInfo build = service.GetType().GetMethod("BuildAuthResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AuthResponse response = (AuthResponse)build.Invoke(service, [user])!;

        Assert.Equal("token", response.Token);
        Assert.NotNull(captured);
        Assert.Contains(captured!, c => c.Type == "tenant_id" && c.Value == user.Id);
        Claim scope = Assert.Single(captured!, c => c.Type == "scope");
        string[] scopes = scope.Value.Split(' ');
        Assert.Contains("catalog:read", scopes);
        Assert.Contains("catalog:write", scopes);
        Assert.Contains("catalog:publish", scopes);
        Assert.Contains("catalog:admin", scopes);
        Assert.DoesNotContain("catalog:ai-tools", scopes);
    }

    private static object CreateService(IJwtHelper jwt)
    {
        // AuthService has a long constructor; resolve it reflectively with mocks so this
        // test does not have to change every time an unrelated dependency is added.
        ConstructorInfo ctor = typeof(AuthService).GetConstructors().Single();
        object?[] args = ctor.GetParameters().Select(p => Resolve(p.ParameterType, jwt)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? Resolve(Type type, IJwtHelper jwt)
    {
        if (type == typeof(IJwtHelper))
        {
            return jwt;
        }

        if (type == typeof(UserManager<ApplicationUser>))
        {
            return new Mock<UserManager<ApplicationUser>>(new Mock<IUserStore<ApplicationUser>>().Object, null!, null!, null!, null!, null!, null!, null!, null!).Object;
        }

        if (type == typeof(SignInManager<ApplicationUser>))
        {
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IOptions<>))
        {
            Type inner = type.GetGenericArguments()[0];
            object value = Activator.CreateInstance(inner) ?? throw new InvalidOperationException(inner.Name);
            return typeof(Options).GetMethod(nameof(Options.Create))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
        {
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()[0]));
        }

        if (type == typeof(IHostEnvironment))
        {
            Mock<IHostEnvironment> env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns("Development");
            return env.Object;
        }

        if (type.IsInterface)
        {
            return ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!).Object;
        }

        return null;
    }
}
