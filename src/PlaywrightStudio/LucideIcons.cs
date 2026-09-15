namespace PlaywrightStudio;

/// <summary>
/// A handful of icons from Lucide (https://lucide.dev, ISC licence), copied in rather than
/// fetched - the studio makes no outbound requests and has to work offline.
///
/// They are stroked rather than filled, which reads a good deal lighter than Material's solid
/// set at the sizes the home tiles use. Stroke width is the weight dial: 1.5 is the house
/// setting, 1.25 looks anaemic below 20px and 2 starts to look heavy above 40px.
/// </summary>
public static class LucideIcons
{
    private const string Open =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" "
        + "stroke=\"currentColor\" stroke-width=\"1.5\" stroke-linecap=\"round\" "
        + "stroke-linejoin=\"round\" width=\"100%\" height=\"100%\">";

    private const string Close = "</svg>";

    private static string Icon(string body) => Open + body + Close;

    /// <summary>circle-dot - a record button.</summary>
    public static readonly string Record = Icon(
        "<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='3.5' fill='currentColor' stroke='none'/>"
            .Replace('\'', '"'));

    /// <summary>upload.</summary>
    public static readonly string Import = Icon(
        "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='M17 8l-5-5-5 5'/><path d='M12 3v12'/>"
            .Replace('\'', '"'));

    /// <summary>list.</summary>
    public static readonly string Scenarios = Icon(
        ("<path d='M8 6h13'/><path d='M8 12h13'/><path d='M8 18h13'/>"
       + "<path d='M3 6h.01'/><path d='M3 12h.01'/><path d='M3 18h.01'/>")
            .Replace('\'', '"'));

    /// <summary>history.</summary>
    public static readonly string Runs = Icon(
        ("<path d='M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8'/>"
       + "<path d='M3 3v5h5'/><path d='M12 7v5l4 2'/>")
            .Replace('\'', '"'));

    /// <summary>book-open.</summary>
    public static readonly string Manuals = Icon(
        ("<path d='M12 7v14'/>"
       + "<path d='M3 18a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h5a4 4 0 0 1 4 4 4 4 0 0 1 4-4h5a1 1 0 0 1 1 1v13"
       + "a1 1 0 0 1-1 1h-6a3 3 0 0 0-3 3 3 3 0 0 0-3-3z'/>")
            .Replace('\'', '"'));

    /// <summary>settings.</summary>
    public static readonly string Settings = Icon(
        ("<path d='M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08"
       + "a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74"
       + "l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73"
       + "V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08"
       + "a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74"
       + "l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25"
       + "a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z'/><circle cx='12' cy='12' r='3'/>")
            .Replace('\'', '"'));

    /// <summary>circle-help.</summary>
    public static readonly string Help = Icon(
        ("<circle cx='12' cy='12' r='10'/>"
       + "<path d='M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3'/><path d='M12 17h.01'/>")
            .Replace('\'', '"'));

    /// <summary>layout-grid - the home screen.</summary>
    public static readonly string Home = Icon(
        ("<rect width='7' height='7' x='3' y='3' rx='1'/><rect width='7' height='7' x='14' y='3' rx='1'/>"
       + "<rect width='7' height='7' x='14' y='14' rx='1'/><rect width='7' height='7' x='3' y='14' rx='1'/>")
            .Replace('\'', '"'));
}
