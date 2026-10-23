namespace Sonar.Models;

public enum MdBlockType
{
    Paragraph,
    Heading,
    Quote,
    Bullet,
    Numbered,
    CodeBlock,
    Table,
    Rule,
    Blank,
}

public sealed class MdInline
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Code { get; set; }
}

public sealed class MdBlock
{
    public MdBlockType Type { get; set; }
    public int Level { get; set; }
    public List<MdInline> Inlines { get; set; } = new();
    public List<string> Lines { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public bool HasHeader { get; set; }
    public string Number { get; set; } = "";
}
