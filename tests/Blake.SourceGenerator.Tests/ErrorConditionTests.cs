using Blake.SourceGenerator;
using Xunit;

namespace Blake.SourceGenerator.Tests;

/// <summary>
/// Tests for error conditions when parsing malformed Blake files.
/// </summary>
public class ErrorConditionTests
{
    [Fact]
    public void UnclosedMetaBlock_ThrowsParseException()
    {
        var blakeSource = @"@{|
    var x = 10;
";  // Missing |}

        var parser = new BlakeParser(blakeSource, "test.blake");
        var ex = Assert.Throws<BlakeParseException>(() => parser.Parse());

        // Verify specific error message and position (EOF after line 2)
        Assert.Contains("Parse error at line 3, column 1: expected |}", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public void UnclosedQuasiQuote_ThrowsParseException()
    {
        var blakeSource = @"@{|
    `This is unclosed
|}";

        var parser = new BlakeParser(blakeSource, "test.blake");
        var ex = Assert.Throws<BlakeParseException>(() => parser.Parse());

        // Verify specific error message and position (EOF after |})
        Assert.Contains("Parse error at line 3, column 3: unclosed quasi-quote", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(3, ex.Column);
    }

    [Fact]
    public void UnclosedSplice_ThrowsParseException()
    {
        var blakeSource = @"@{|
    `Value: @(someExpr`
|}";  // Missing closing ) for splice

        var parser = new BlakeParser(blakeSource, "test.blake");
        var ex = Assert.Throws<BlakeParseException>(() => parser.Parse());

        // Verify specific error message and position (EOF after consuming content looking for ))
        Assert.Contains("Parse error at line 3, column 3: expected closing )", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(3, ex.Column);
    }
}
