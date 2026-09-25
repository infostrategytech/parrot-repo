using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Renders text in one of the Figma text styles.
///
/// The element is chosen by the caller because the style says how text is
/// shaped, not what it means: a heading style on a <c>span</c> is a visual
/// choice, an <c>h2</c> is a structural one, and conflating them is how a
/// document ends up with no heading outline.
/// </summary>
public sealed class Typography : ComponentBase
{
    // React allowed exactly these tags. Keeping the list closed stops
    // Typography becoming a way to render arbitrary markup.
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "aside", "b", "code", "dd", "div", "dt", "em", "h1", "h2", "h3", "h4",
        "li", "p", "small", "span", "strong", "summary", "time"
    };

    [Parameter, EditorRequired]
    public TextStyle TextStyle { get; set; }

    [Parameter, EditorRequired]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>The element to render. Defaults to <c>span</c>.</summary>
    [Parameter]
    public string As { get; set; } = "span";

    /// <summary>
    /// Opt into the leading scale. Every Figma text style specifies
    /// line-height 100%, which is right for the single-line labels they were
    /// drawn for and too tight for anything that wraps.
    /// </summary>
    [Parameter]
    public bool Prose { get; set; }

    [Parameter]
    public string? Class { get; set; }

    /// <summary>
    /// Any other attribute — <c>id</c>, <c>title</c>, <c>aria-*</c>, an event
    /// handler — is passed straight through to the element.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    protected override void OnParametersSet()
    {
        if (!AllowedElements.Contains(As))
        {
            throw new ArgumentException(
                $"Typography cannot render <{As}>. Allowed: {string.Join(", ", AllowedElements.Order())}.",
                nameof(As));
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, As);
        // Splat first so a caller can never clobber the text style class.
        builder.AddMultipleAttributes(1, AdditionalAttributes);
        builder.AddAttribute(2, "class", ClassName());
        builder.AddContent(3, ChildContent);
        builder.CloseElement();
    }

    private string ClassName()
    {
        var style = TextStyle.ToClassName(Prose);
        return string.IsNullOrWhiteSpace(Class) ? style : $"{style} {Class}";
    }
}
