using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public sealed class ADGroupRulesModel : PageModel
{
    public IActionResult OnGet()
    {
        var queryString = Request.QueryString.HasValue
            ? Request.QueryString.Value
            : string.Empty;

        return Redirect($"/Settings/GroupRules{queryString}");
    }
}
