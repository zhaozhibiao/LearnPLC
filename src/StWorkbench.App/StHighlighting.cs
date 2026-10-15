using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
namespace StWorkbench.App;
internal static class StHighlighting
{
    public static IHighlightingDefinition Create()
    {
        var words = string.Join("", StWorkbench.Core.StLanguageService.Keywords.Select(k => "<Word>" + k + "</Word>"));
        var xml = """
        <SyntaxDefinition name="ST" extensions=".st" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6F9B83" />
          <Color name="String" foreground="#DCAA7C" />
          <Color name="Keyword" foreground="#79B8FF" fontWeight="bold" />
          <Color name="Number" foreground="#B5CEA8" />
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="//" />
            <Span color="Comment" multiline="true" begin="\(\*" end="\*\)" />
            <Span color="String" begin="'" end="'"><RuleSet><Span begin="\$" end="." /></RuleSet></Span>
            <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\$" end="." /></RuleSet></Span>
            <Keywords color="Keyword">WORDS</Keywords>
            <Rule color="Number">\b[0-9]+(\.[0-9]+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """.Replace("WORDS", words);
        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
