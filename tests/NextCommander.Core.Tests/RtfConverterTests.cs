using NextCommander.Core.Text;

namespace NextCommander.Core.Tests;

public sealed class RtfConverterTests
{
    [Fact]
    public void Converts_paragraphs_formatting_and_characters()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1254\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red255\green0\blue0;}"
            + @"{\*\generator Test;}\f0 Merhaba \b kal\'fdn\b0  ve \i italik\i0 , \ul alt\ulnone .\par"
            + @"\u351?ehir \'fe\tab sekme\line yeni sat\u305?r\par {\pict\pngblip 89504e47}Son}";
        var html = RtfConverter.ToHtml(rtf);
        Assert.Equal(
            "<p>Merhaba <b>kalın</b> ve <i>italik</i>, <u>alt</u>.</p>\n"
            + "<p>şehir ş&emsp;sekme<br>yeni satır</p>\n"
            + "<p>Son</p>\n", html);
        Assert.DoesNotContain("Arial", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Test", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_html_and_symbols()
    {
        var html = RtfConverter.ToHtml(@"{\rtf1 a<b> \{x\} \\ \emdash\~c}");
        Assert.Equal("<p>a&lt;b&gt; {x} \\ \u2014&#160;c</p>\n", html);
        Assert.True(RtfConverter.LooksLikeRtf("{\\rtf1 x}"u8));
        Assert.False(RtfConverter.LooksLikeRtf("hello"u8));
    }
}
