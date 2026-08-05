using System.Net;

namespace EndpointSubmission.Service.Pages.Components;

/// <summary>
/// Escaping helper for the interpolated-HTML pages.
///
/// Pages are built by string interpolation, which means any value that did not come from the
/// page source itself is an injection vector — and most of what these pages display does not:
/// ARC project names and submitter addresses come from the DataHUB, error text comes from
/// upstream services, and <c>package_payload</c> fields come straight from the hook caller.
///
/// The rule is that <c>$"{value}"</c> inside a page is a bug unless <paramref name="value"/>
/// went through <see cref="Text"/> first. Only literal markup written in these files may be
/// interpolated raw.
/// </summary>
public static class Html
{
    /// <summary>Escapes a value for use as element text or inside a quoted attribute.</summary>
    public static string Text(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
