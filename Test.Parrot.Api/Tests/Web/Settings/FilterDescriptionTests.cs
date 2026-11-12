using Microsoft.AspNetCore.Components;
using Parrot.SharedComponents.DesignSystem;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Dropdown options are labels only (designer, 7 November 2026), set once in the component: PopoverOption has no
// way to take a description, and FilterPopover no switch to allow one, so no screen can add a second line back.
public sealed class FilterDescriptionTests
{
    [Theory]
    [InlineData(typeof(PopoverOption), "Description")]
    [InlineData(typeof(PopoverOption), "DescriptionContent")]
    [InlineData(typeof(FilterPopover), "Describe")]
    public void DropdownsHaveNoDescriptionSetting(Type component, string parameter)
    {
        var property = component.GetProperty(parameter);
        Assert.True(property is null || !Attribute.IsDefined(property, typeof(ParameterAttribute)),
            $"{component.Name}.{parameter} should not exist: dropdown options are labels only.");
    }
}
