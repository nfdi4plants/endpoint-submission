namespace EndpointSubmission.Service.Pages.Components;

/// <summary>
/// The page shell, mirroring AVPR's <c>Pages/Components/Layout.cs</c>: plain interpolated HTML,
/// PicoCSS served from wwwroot rather than a CDN, and <c>color-scheme: light dark</c> so the
/// pages follow the reader's system theme without any JavaScript.
///
/// Deliberately no navbar: unlike AVPR there is nothing to navigate to. Every page here is
/// reached from a badge click or a signed status link, so a nav would only offer routes the
/// visitor has no token for.
/// </summary>
public static class Layout
{
    public static string Render(string title, string content, string additionalHeadContent = "")
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
  <head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
    <meta name=""color-scheme"" content=""light dark"" />
    <!-- Status pages carry a token in the query string; keep it out of Referer headers. -->
    <meta name=""referrer"" content=""no-referrer"" />
    <link rel=""stylesheet"" href=""/css/pico.cyan.min.css"" />
    {additionalHeadContent}
    <title>{Html.Text(title)}</title>
  </head>
  <body>
    <main class=""container"">
      {content}
    </main>
  </body>
</html>";
    }
}
