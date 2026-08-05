namespace EndpointSubmission.Service.Pages.Components;

/// <summary>
/// Landing page. There is nothing actionable here on purpose — submissions are only reachable
/// from a badge click or the signed link that produces, so this page exists to tell a visitor
/// who arrived at the bare host what the service is and why they cannot do anything from here.
/// </summary>
public static class Index
{
    public static string Render() => @"
      <hgroup>
        <h1>ARC Endpoint Submission</h1>
        <p>An ARC App for submitting ARC contents to endpoint repositories.</p>
      </hgroup>

      <p>
        This service is triggered by clicking a validation package badge on an ARC's DataHUB
        homepage. The badge is only clickable once the validation package passes, so an ARC that
        reaches this point has already been checked; this service captures its submission inputs,
        presents an endpoint-specific preview, and tracks the resulting transaction.
      </p>

      <p>
        There is nothing to start from this page. Submissions are reached from the badge, and their
        status pages from the link handed out when the submission was opened.
      </p>

      <footer>
        <small>
          <a href=""/swagger"">API documentation</a> &middot;
          <a href=""/_health"">Health</a> &middot;
          <a href=""https://github.com/nfdi4plants/endpoint-submission"">Source</a>
        </small>
      </footer>";
}
