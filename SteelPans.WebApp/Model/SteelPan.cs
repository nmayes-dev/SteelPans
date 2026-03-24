namespace SteelPans.WebApp.Model;

public enum PanType
{
    Lead
}

public enum PanNoteKind
{
    AFlat1,
    A1,
    ASharp1,
    BFlat1 = ASharp1,
    B1,
    BSharp1,
    C1 = BSharp1,
    CSharp1,
    DFlat1 = CSharp1,
    D1,
    DSharp1,
    EFlat1 = DSharp1,
    E1,
    ESharp1,
    FFlat1 = ESharp1,
    F1,
    FSharp1,
    GFlat1 = FSharp1,
    G1,
    GSharp1,
    AFlat2 = GSharp1,
    A2,

}

public class SteelPan
{
    public PanType PanType { get; set; } 

    public string? BaseSvg { get; set; }

    public Dictionary<PanNoteKind, string> PanNotes { get; set; } = new();
}
