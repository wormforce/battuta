namespace Battuta.Core.Input;

public enum KeyboardRowId
{
    R0,
    R1,
    R2,
    R3,
    R4,
}

public enum KeyboardSpecialKeyId
{
    Space,
    Enter,
    Backspace,
}

public enum KeyboardLayoutMembership
{
    Compact,
    Extended,
    PlatformOnly,
    InputMethod,
}

public sealed record KeyboardKeyDefinition(
    PhysicalKeyId Id,
    string Label,
    KeyboardRowId Row,
    KeyboardSpecialKeyId? SpecialKey,
    double WidthUnits,
    string? LegacySoundPackV1Id,
    KeyboardLayoutMembership Membership,
    string LayoutRowId,
    bool IsAssignable = true);

public sealed record KeyboardLayoutRow(string Id, IReadOnlyList<KeyboardKeyDefinition> Keys);

public sealed record KeyboardLayoutDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<KeyboardLayoutRow> Rows)
{
    public IReadOnlyList<KeyboardKeyDefinition> Keys { get; } = Rows.SelectMany(row => row.Keys).ToArray();
}
