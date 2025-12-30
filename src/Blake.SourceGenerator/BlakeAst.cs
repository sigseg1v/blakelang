using System.Collections.Generic;

namespace Blake.SourceGenerator;

/// <summary>
/// Base class for all Blake AST nodes.
/// </summary>
public abstract class BlakeNode
{
    public int StartPosition { get; set; }
    public int EndPosition { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
}

/// <summary>
/// Represents an entire .blake file.
/// </summary>
public class BlakeFile : BlakeNode
{
    public string FilePath { get; set; } = "";
    public List<BlakeNode> Children { get; } = new();
}

/// <summary>
/// Represents a block of regular C# code that passes through unchanged.
/// Everything outside @{| |} blocks.
/// </summary>
public class CSharpPassthrough : BlakeNode
{
    public string Code { get; set; } = "";
}

/// <summary>
/// Represents a meta-block: @{| ... |}
/// Contains C# code executed at compile time, plus quasi-quoted output templates.
/// </summary>
public class MetaBlock : BlakeNode
{
    /// <summary>
    /// The C# meta-code to execute, with quasi-quotes replaced by __emit_N__() placeholders.
    /// </summary>
    public string Code { get; set; } = "";
    
    /// <summary>
    /// The quasi-quoted output templates found in this meta-block.
    /// These correspond to the __emit_N__() placeholders in Code.
    /// </summary>
    public List<QuasiQuote> QuasiQuotes { get; } = new();
}

/// <summary>
/// Represents a quasi-quoted output template: `...`
/// Contains literal text and splice expressions that produce C# output.
/// </summary>
public class QuasiQuote : BlakeNode
{
    /// <summary>
    /// The parts of this quasi-quote (text and splices interleaved).
    /// </summary>
    public List<QuasiQuotePart> Parts { get; } = new();

    /// <summary>
    /// True if this is an inline quasi-quote (no implicit newline should be added).
    /// False if it's a block quasi-quote (implicit newline should be added).
    /// </summary>
    public bool IsInline { get; set; }
}

/// <summary>
/// Base class for parts of a quasi-quote.
/// </summary>
public abstract class QuasiQuotePart : BlakeNode
{
}

/// <summary>
/// Literal text within a quasi-quote.
/// </summary>
public class QuasiQuoteText : QuasiQuotePart
{
    public string Text { get; set; } = "";
}

/// <summary>
/// A splice expression within a quasi-quote: @(expr)
/// The expression is evaluated and its result interpolated into output.
/// </summary>
public class QuasiQuoteSplice : QuasiQuotePart
{
    public string Expression { get; set; } = "";
}
