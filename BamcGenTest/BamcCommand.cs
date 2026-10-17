using Parsley.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace BamcTest;

public record LineInfo(long Position, int Line, int Column)
{
    internal static LineInfo FromNode(ParseNode node)
    {
        return new LineInfo(node.Position, node.Line, node.Column);
    }
};

public abstract class BamcCommand
{
    public LineInfo Location { get; }
    protected BamcCommand(LineInfo location)
    {
        Location = location;
    }
}
public class BamcBrowserCommand : BamcCommand
{
    public int BrowserId { get; } // could make this an enum but is more maintainence 
    public BamcBrowserCommand(int browserId, LineInfo location) : base(location)
    {
        if(browserId<0 || browserId>(BamcParser.Safari-BamcParser.Brave))
        {
            throw new ArgumentOutOfRangeException(nameof(browserId),browserId,"The browser id was invalid");
        }
        BrowserId = browserId;
    }
}

public class BamcFeatureCommand : BamcCommand
{
    public int FeatureId { get; } // could make this an enum but is more maintainence 
    public string? Argument { get; }
    public BamcFeatureCommand(int featureId, string? argument, LineInfo location) : base(location)
    {
        if (featureId < 0 || featureId > (BamcParser.UseMobileUserAgentFeatureKeyword - BamcParser.AddExtensionFeatureKeyword))
        {
            throw new ArgumentOutOfRangeException(nameof(featureId), featureId, "The feature id was invalid");
        }
        FeatureId = featureId;
        Argument = argument;
    }
}


public class BamcVisitCommand : BamcCommand
{
    public Uri Url { get; }
    public BamcVisitCommand(Uri url, LineInfo location) : base(location)
    {
        ArgumentNullException.ThrowIfNull(url, nameof(url));
        Url = url;
    }
}

public class BamcWaitForSecondsCommand : BamcCommand
{
    public float Seconds { get; }
    public BamcWaitForSecondsCommand(float seconds, LineInfo location) : base(location)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds,nameof(seconds));
        Seconds = seconds;
    }
}
public class BamcFillTextCommand : BamcCommand
{
    public string Field { get; }
    public string Text { get; }
    public BamcFillTextCommand(string field, string text, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(field, nameof(field));
        ArgumentNullException.ThrowIfNull(text, nameof(text));
        Field = field;
        Text = text;
    }
}
public class BamcFillTextExpCommand : BamcCommand
{
    public string Expression { get; }
    public string Text { get; }
    public BamcFillTextExpCommand(string expression, string text, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        ArgumentNullException.ThrowIfNull(text, nameof(text));
        Expression = expression;
        Text = text;
    }
}
public class BamcOpenNewTabCommand : BamcCommand
{
    public Uri Url { get; }
    public string Tab { get; }
    public BamcOpenNewTabCommand(Uri url, string tab, LineInfo location) : base(location)
    {
        ArgumentNullException.ThrowIfNull(url, nameof(url));
        ArgumentException.ThrowIfNullOrEmpty(tab, nameof(tab));
        Url = url;
        Tab = tab;
    }
}
public class BamcAddCookieCommand : BamcCommand
{
    public string Name { get; }
    public string Value { get; }
    public BamcAddCookieCommand(string name, string value, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value, nameof(value));
        Name = name;
        Value = value;
    }
}
public class BamcAddHeadersCommand : BamcCommand
{
    public IReadOnlyDictionary<string, string> Headers { get; }
    public BamcAddHeadersCommand(IDictionary<string, object?> json, LineInfo location) : base(location)
    {
        var dic = new Dictionary<string, string>();
        ArgumentNullException.ThrowIfNull(json, nameof(json));
        foreach (var kvp in json)
        {
            var sw = new StringWriter();
            BamcParser.JsonEmbed.Evaluator.WriteTo(json, sw, true);
            dic.Add(kvp.Key, sw.ToString());
        }
        Headers = dic;
    }
    public BamcAddHeadersCommand(string key, string value, LineInfo location) : base(location)
    {
        var dic = new Dictionary<string, string>();
        ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
        ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
        dic.Add(key, value);
        Headers = dic;
    }
}
public class BamcTakeScreenshotCommand : BamcCommand
{
    public string FilePath { get; }
    public BamcTakeScreenshotCommand(string filePath,LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath, nameof(filePath));
        if(0<filePath.IndexOfAny(Path.GetInvalidPathChars(),0))
        {
            throw new ArgumentException("The file path is not valid", nameof(filePath));
        }
        FilePath = filePath;
    }
}
public class BamcSaveAsHtmlCommand : BamcCommand
{
    public string FilePath { get; }
    public BamcSaveAsHtmlCommand(string filePath, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath, nameof(filePath));
        if (0 < filePath.IndexOfAny(Path.GetInvalidPathChars(), 0))
        {
            throw new ArgumentException("The file path is not valid", nameof(filePath));
        }
        FilePath = filePath;
    }
}
public class BamcSaveAsHtmlExpCommand : BamcCommand
{
    public string Expression { get; }
    public BamcSaveAsHtmlExpCommand(string expression, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        Expression = expression;
    }
}
public class BamcCloseCurrentTabCommand : BamcCommand
{
    public BamcCloseCurrentTabCommand(LineInfo location) : base(location)
    {
        
    }
}


public class BamcSetCustomUserAgentCommand : BamcCommand
{
    public string Agent { get; }
    public BamcSetCustomUserAgentCommand(string agent, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(agent, nameof(agent));
        Agent= agent;
    }
}

public class BamcGetTextCommand : BamcCommand
{
    public string Field { get; }
    public BamcGetTextCommand(string field, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(field, nameof(field));
        Field = field;
    }
}

public class BamcClickCommand : BamcCommand
{
    public string Field { get; }
    public BamcClickCommand(string field, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(field, nameof(field));
        Field = field;
    }
}

public class BamcClickExpCommand : BamcCommand
{
    public string Expression { get; }
    public BamcClickExpCommand(string expression, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        Expression = expression;
    }
}

public class BamcClickAtPositionCommand : BamcCommand
{
    public string X { get; }
    public string Y { get; }
    public BamcClickAtPositionCommand(string x, string y, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(x, nameof(x));
        ArgumentException.ThrowIfNullOrEmpty(y, nameof(y));
        X = x;
        Y = y;
    }
}

public class BamcSelectElementCommand : BamcCommand
{
    public string Selector { get; }
    public BamcSelectElementCommand(string selector, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector, nameof(selector));
        Selector = selector;
    }
}

public class BamcSelectOptionCommand : BamcCommand
{
    public string Selector { get; }
    public int Index { get; }
    public BamcSelectOptionCommand(string selector, int index, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector, nameof(selector));
        ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(index));
        Selector = selector;
        Index = index;
    }
}

public class BamcJavaScriptCommand : BamcCommand
{
    public string Script { get; }
    public BamcJavaScriptCommand(string script, LineInfo location) : base(location)
    {
        ArgumentException.ThrowIfNullOrEmpty(script, nameof(script));
        Script = script;
    }
}