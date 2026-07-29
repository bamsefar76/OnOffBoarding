using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

public class LogoutModel : PageModel
{
    private readonly UiTextService _uiTextService;

    public LogoutModel(UiTextService uiTextService)
    {
        _uiTextService = uiTextService;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var fallbackTexts = new Dictionary<string, string>
        {
            ["logout.title"] = "Signed out",
            ["logout.heading"] = "Signed out",
            ["logout.closeBrowsers"] = "Close all browser windows or purge Kerberos tickets:",
            ["logout.reopenBrowser"] = "Then reopen the browser."
        };

        var uiText = await _uiTextService.GetTextsAsync(HttpContext, fallbackTexts);
        string T(string key) => WebUtility.HtmlEncode(uiText.T(key, fallbackTexts[key]));

        return Content($@"
<html>
<head>
    <title>{T("logout.title")}</title>
</head>
<body>
    <h2>{T("logout.heading")}</h2>

    <p>
        {T("logout.closeBrowsers")}
    </p>

    <pre>klist purge</pre>

    <p>
        {T("logout.reopenBrowser")}
    </p>
</body>
</html>", "text/html");
    }
}
