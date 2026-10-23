namespace Parrot.Web.Features.Auth;

/// <summary>
/// Put on a page: the access rule (Parrot.SharedComponents.Layout.AccessRules)
/// a person needs to open it. The router checks it before the page renders, so
/// a page the person's role does not reach never loads; they see the
/// no-access screen in its place. A page without it is open to every member.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresAccessAttribute(string rule) : Attribute
{
    public string Rule { get; } = rule;
}
