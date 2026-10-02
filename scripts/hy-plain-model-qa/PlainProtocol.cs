internal static class PlainProtocol
{
    internal static string Prompt(string source,string target)
    {
        var name=target switch {"en-US"=>"英语","zh-CN"=>"简体中文",_=>throw new ArgumentException("Unsupported target")};
        return $"将以下文本翻译为{name}，注意只需要输出翻译后的结果，不要额外解释：\n{source}";
    }
    internal static bool ValidLine(string text,string source)=>!string.IsNullOrWhiteSpace(text) && !text.Contains('\n') && !text.Contains('\r') && !text.Contains("```") && !string.Equals(text.Trim(),source.Trim(),StringComparison.Ordinal);
    internal static void SelfTest()
    {
        if(Prompt("原句", "en-US")!="将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n原句") throw new Exception("Official template changed");
        if(Prompt("Original", "zh-CN")!="将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\nOriginal") throw new Exception("Target changed");
        if(!ValidLine("Translated.","Original.") || ValidLine("Original.","Original.") || ValidLine("first\nsecond","Original.") || ValidLine("","Original.")) throw new Exception("Whole-response validation failed");
        var fixture=System.Text.Json.JsonSerializer.Deserialize<FreshFixture>("{\"Lines\":[{\"Id\":0,\"Language\":\"ja\",\"Text\":\"本文\",\"SemanticCheck\":\"REVIEW_ONLY_SENTINEL\"}]}")!;
        if(Prompt(fixture.Lines[0].Text,"en-US").Contains("REVIEW_ONLY_SENTINEL")) throw new Exception("Review annotation reached prompt");
        Console.WriteLine("Plain protocol checks passed: exact official template/full target names, no foreign-source copying, no extra lines");
    }
}
