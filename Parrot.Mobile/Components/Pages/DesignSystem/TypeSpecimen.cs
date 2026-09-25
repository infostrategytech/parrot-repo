using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Mobile.Components.Pages.DesignSystem;

/// <summary>One row of the typography specimen.</summary>
/// <param name="Style">The style itself, so the row renders what it documents.</param>
/// <param name="Spec">Family and size, as the Figma style names them.</param>
/// <param name="Sample">Text long enough to show the style doing its job.</param>
/// <param name="Use">Where this style belongs, when that is not obvious.</param>
public sealed record TypeSpecimenRow(TextStyle Style, string Spec, string Sample, string? Use = null);

public sealed record TypeSpecimenGroup(string Name, IReadOnlyList<TypeSpecimenRow> Rows);

/// <summary>
/// Mirrors the Figma specimen frame at node 138:2 so a design review can read
/// down the frame and the page in the same order.
/// </summary>
public static class TypeSpecimen
{
    public static readonly IReadOnlyList<TypeSpecimenGroup> Groups =
    [
        new("Display · 36px · display family",
        [
            new(TextStyle.DisplayMedium, "Geist Medium · 36px", "Display Medium", "Subtle hero text"),
            new(TextStyle.DisplaySemibold, "Geist SemiBold · 36px", "Display SemiBold", "Section headers"),
            new(TextStyle.DisplayBold, "Geist Bold · 36px", "Display Bold", "Hero headlines"),
            new(TextStyle.DisplayExtrabold, "Geist ExtraBold · 36px", "Display ExtraBold", "Maximum impact")
        ]),

        new("Headings",
        [
            new(TextStyle.HeadingH1Regular, "Geist Regular · 30px", "Heading 1 Regular"),
            new(TextStyle.HeadingH1Medium, "Geist Medium · 30px", "Heading 1 Medium"),
            new(TextStyle.HeadingH1Semibold, "Geist SemiBold · 30px", "Heading 1 SemiBold"),
            new(TextStyle.HeadingH1Bold, "Geist Bold · 30px", "Heading 1 Bold", "Page titles"),
            new(TextStyle.HeadingH2Regular, "Geist Regular · 24px", "Heading 2 Regular"),
            new(TextStyle.HeadingH2Medium, "Geist Medium · 24px", "Heading 2 Medium"),
            new(TextStyle.HeadingH2Semibold, "Geist SemiBold · 24px", "Heading 2 SemiBold", "Section titles"),
            new(TextStyle.HeadingH2Bold, "Geist Bold · 24px", "Heading 2 Bold"),
            new(TextStyle.HeadingH3Regular, "Geist Regular · 20px", "Heading 3 Regular"),
            new(TextStyle.HeadingH3Medium, "Geist Medium · 20px", "Heading 3 Medium"),
            new(TextStyle.HeadingH3Semibold, "Geist SemiBold · 20px", "Heading 3 SemiBold", "Card titles"),
            new(TextStyle.HeadingH3Bold, "Geist Bold · 20px", "Heading 3 Bold"),
            new(TextStyle.HeadingH4Regular, "Geist Regular · 18px", "Heading 4 Regular"),
            new(TextStyle.HeadingH4Medium, "Geist Medium · 18px", "Heading 4 Medium", "Subsections"),
            new(TextStyle.HeadingH4Semibold, "Geist SemiBold · 18px", "Heading 4 SemiBold"),
            new(TextStyle.HeadingH4Bold, "Geist Bold · 18px", "Heading 4 Bold")
        ]),

        new("Body",
        [
            new(TextStyle.BodyLargeRegular, "Geist Regular · 18px", "Body Large — The quick brown fox jumps over the lazy dog.", "Intros, leads"),
            new(TextStyle.BodyLargeMedium, "Geist Medium · 18px", "Body Large Medium — The quick brown fox jumps."),
            new(TextStyle.BodyLargeSemibold, "Geist SemiBold · 18px", "Body Large SemiBold — The quick brown fox."),
            new(TextStyle.BodyLargeBold, "Geist Bold · 18px", "Body Large Bold — The quick brown fox."),
            new(TextStyle.BodyDefaultRegular, "Geist Regular · 16px", "Body Default — The quick brown fox jumps over the lazy dog.", "Paragraphs"),
            new(TextStyle.BodyDefaultMedium, "Geist Medium · 16px", "Body Default Medium — The quick brown fox jumps."),
            new(TextStyle.BodyDefaultSemibold, "Geist SemiBold · 16px", "Body Default SemiBold — The quick brown fox."),
            new(TextStyle.BodyDefaultBold, "Geist Bold · 16px", "Body Default Bold — The quick brown fox."),
            new(TextStyle.BodySmallRegular, "Geist Regular · 14px", "Body Small — The quick brown fox jumps over the lazy dog.", "Secondary text"),
            new(TextStyle.BodySmallMedium, "Geist Medium · 14px", "Body Small Medium — The quick brown fox jumps over."),
            new(TextStyle.BodySmallSemibold, "Geist SemiBold · 14px", "Body Small SemiBold — The quick brown fox jumps."),
            new(TextStyle.BodySmallBold, "Geist Bold · 14px", "Body Small Bold — The quick brown fox jumps.")
        ]),

        new("Small",
        [
            new(TextStyle.Small12Regular, "Geist Regular · 12px", "Small 12 Regular — Labels, timestamps, helper text", "Captions"),
            new(TextStyle.Small12Medium, "Geist Medium · 12px", "Small 12 Medium — Labels, timestamps, helper text", "Badges, tags"),
            new(TextStyle.Small12Semibold, "Geist SemiBold · 12px", "Small 12 SemiBold — Overline usage", "Overlines"),
            new(TextStyle.Small12Bold, "Geist Bold · 12px", "Small 12 Bold — Strong labels"),
            new(TextStyle.Small10Regular, "Geist Regular · 10px", "Small 10 Regular — Minimal annotations, fine print", "Micro labels"),
            new(TextStyle.Small10Medium, "Geist Medium · 10px", "Small 10 Medium — Badge counts, compact UI"),
            new(TextStyle.Small10Semibold, "Geist SemiBold · 10px", "Small 10 SemiBold — Emphasis at micro scale")
        ]),

        new("Code · mono family",
        [
            new(TextStyle.Code14Regular, "Geist Mono Regular · 14px", "const token = \"sk_live_abc123\";", "Code blocks"),
            new(TextStyle.Code14Bold, "Geist Mono Bold · 14px", "export default function App() {}", "Emphasised code")
        ])
    ];
}
