namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// The emoji the picker offers: a curated set for customer conversations, in
/// groups, each with the words it is found by. They are plain Unicode text,
/// so every device draws them in its own emoji style (Apple, Microsoft,
/// Google) and they are sent and stored like any other character.
/// </summary>
public static class EmojiCatalog
{
    public sealed record Emoji(string Glyph, string Words);
    public sealed record Group(string Name, IReadOnlyList<Emoji> Items);

    private static Emoji E(string glyph, string words) => new(glyph, words);

    public static readonly IReadOnlyList<Group> Groups =
    [
        new("Smileys", [
            E("😊", "smile happy blush"), E("😀", "grin happy"), E("😁", "beam grin"), E("😂", "joy laugh tears"),
            E("🤣", "rofl laugh"), E("🙂", "slight smile"), E("😉", "wink"), E("😍", "love heart eyes"),
            E("🥰", "love hearts"), E("😘", "kiss"), E("😎", "cool sunglasses"), E("🤩", "star struck excited"),
            E("🤗", "hug"), E("🤔", "thinking"), E("😅", "sweat relief"), E("😇", "angel innocent"),
            E("🙃", "upside down"), E("😌", "relieved calm"), E("😴", "sleep"), E("😢", "cry sad"),
            E("😭", "sob cry"), E("😔", "pensive sad"), E("😕", "confused"), E("😮", "surprised wow"),
            E("😬", "grimace awkward"), E("😤", "frustrated"), E("😡", "angry"), E("🥺", "pleading please"),
        ]),
        new("Gestures", [
            E("👍", "thumbs up yes ok like"), E("👎", "thumbs down no"), E("👌", "ok perfect"), E("🙏", "thanks please pray"),
            E("👏", "clap applause"), E("🙌", "celebrate raise hands"), E("🤝", "handshake deal"), E("👋", "wave hello bye"),
            E("✌️", "peace victory"), E("🤞", "fingers crossed luck"), E("💪", "strong muscle"), E("👀", "eyes look"),
            E("✍️", "writing"), E("🫶", "heart hands love"), E("👉", "point right"), E("☝️", "point up one"),
        ]),
        new("Hearts & celebration", [
            E("❤️", "red heart love"), E("🧡", "orange heart"), E("💛", "yellow heart"), E("💚", "green heart"),
            E("💙", "blue heart"), E("💜", "purple heart"), E("🖤", "black heart"), E("💯", "hundred perfect"),
            E("✨", "sparkles"), E("🎉", "party celebrate"), E("🎊", "confetti"), E("🥳", "party face birthday"),
            E("🎁", "gift present"), E("🎂", "birthday cake"), E("🌟", "star glow"), E("🔥", "fire hot lit"),
        ]),
        new("Shopping & delivery", [
            E("🛍️", "shopping bags"), E("🛒", "cart shopping"), E("📦", "package parcel delivery"), E("🚚", "delivery truck"),
            E("🛵", "dispatch rider motorbike"), E("🏷️", "tag price label"), E("💳", "card payment"), E("💵", "cash money"),
            E("💰", "money bag"), E("🧾", "receipt"), E("📍", "location pin address"), E("🗓️", "calendar date"),
            E("⏰", "alarm time"), E("⏳", "waiting hourglass"), E("📞", "phone call"), E("📩", "message envelope"),
        ]),
        new("Food & drink", [
            E("🍛", "rice curry jollof"), E("🍗", "chicken"), E("🍔", "burger"), E("🍟", "fries chips"),
            E("🍕", "pizza"), E("🌯", "wrap shawarma"), E("🥗", "salad"), E("🍰", "cake dessert"),
            E("🥤", "drink soda"), E("☕", "coffee tea"), E("🧃", "juice"), E("🍹", "cocktail"),
        ]),
        new("Symbols", [
            E("✅", "check done yes"), E("☑️", "checkbox"), E("❌", "cross no wrong"), E("⚠️", "warning"),
            E("❓", "question"), E("❗", "exclamation important"), E("ℹ️", "information"), E("🔔", "bell notification"),
            E("⭐", "star rating"), E("🆕", "new"), E("🔄", "refresh repeat"), E("➡️", "arrow right next"),
        ]),
    ];
}
