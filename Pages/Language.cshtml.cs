using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

public class LanguageModel : PageModel
{
    private readonly UiTextService _uiTextService;

    public LanguageModel(UiTextService uiTextService)
    {
        _uiTextService = uiTextService;
    }

    public async Task<IActionResult> OnGetAsync(string? culture, string? returnUrl)
    {
        var resolvedLanguageCode = await _uiTextService.ResolveActiveLanguageCodeAsync(culture);

        Response.Cookies.Append(
            UiTextService.LanguageCookieName,
            resolvedLanguageCode,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = false,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return LocalRedirect("/");
    }
}
