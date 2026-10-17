using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace BamcTest;

public abstract class BamcCommand
{
}
public class BamcBrowserCommand : BamcCommand
{
    public int BrowserId { get; } // could make this an enum but is more maintainence 
    public BamcBrowserCommand(int browserId)
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
    public BamcFeatureCommand(int featureId, string? argument)
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
    public BamcVisitCommand(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url, nameof(url));
        Url = url;
    }
}

public class BamcWaitForSecondsCommand : BamcCommand
{
    public float Seconds { get; }
    public BamcWaitForSecondsCommand(float seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds,nameof(seconds));
        Seconds = seconds;
    }
}
public class BamcFillTextCommand : BamcCommand
{
    public string Field { get; }
    public string Text { get; }
    public BamcFillTextCommand(string field, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(field, nameof(field));
        ArgumentNullException.ThrowIfNull(text, nameof(text));
        Field = field;
        Text = text;
    }
}

public class BamcAddHeadersCommand : BamcCommand
{
    public IReadOnlyDictionary<string, string> Headers { get; }
    public BamcAddHeadersCommand(IDictionary<string,object?> json)
    {
        var dic = new Dictionary<string, string>();
        ArgumentNullException.ThrowIfNull(json, nameof(json));
        foreach(var kvp in json)
        {
            var sw = new StringWriter();
            JsonEmbedEvaluator.WriteTo(json, sw,true);
            dic.Add(kvp.Key,  sw.ToString());
        }
        Headers = dic;
    }
    public BamcAddHeadersCommand(string key, string value)
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
    public BamcTakeScreenshotCommand(string filePath)
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
    public BamcSaveAsHtmlCommand(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath, nameof(filePath));
        if (0 < filePath.IndexOfAny(Path.GetInvalidPathChars(), 0))
        {
            throw new ArgumentException("The file path is not valid", nameof(filePath));
        }
        FilePath = filePath;
    }
}