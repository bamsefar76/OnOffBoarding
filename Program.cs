using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.AspNetCore.Server.IISIntegration;
using UserChangeQueueWeb.Filters;
using UserChangeQueueWeb.Services;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseHttpSys(options =>
    {
        options.UrlPrefixes.Add("http://*:5163");
        options.Authentication.Schemes =
            AuthenticationSchemes.Negotiate |
            AuthenticationSchemes.NTLM;
        options.Authentication.AllowAnonymous = false;
    });

    builder.Services.AddAuthentication(HttpSysDefaults.AuthenticationScheme);
}
else
{
    builder.WebHost.UseIISIntegration();
    builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
}

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddScoped<SqlConnectionFactory>();
builder.Services.AddScoped<PageAccessService>();
builder.Services.AddScoped<AccessScopeService>();
builder.Services.AddScoped<ObjectAccessService>();
builder.Services.AddScoped<QueueAuditService>();
builder.Services.AddScoped<ADGroupRuleService>();
builder.Services.AddScoped<OfficeLicenseRuleService>();
builder.Services.AddScoped<AccessCardGroupService>();
builder.Services.AddScoped<UiTextService>();
builder.Services.AddScoped<PersonMatchingService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<PageAccessFilter>();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.ServiceFilterAttribute(typeof(PageAccessFilter)));
});

var app = builder.Build();

var supportedCultures = new[]
{
    new CultureInfo("nb-NO"),
    new CultureInfo("en-GB"),
    new CultureInfo("sv-SE"),
    new CultureInfo("da-DK"),
    new CultureInfo("fi-FI"),
    new CultureInfo("nl-NL"),
    new CultureInfo("fr-FR")
};

var requestLocalizationOptions = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("nb-NO"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures
};

requestLocalizationOptions.RequestCultureProviders.Insert(0,
    new CustomRequestCultureProvider(context =>
    {
        context.Request.Cookies.TryGetValue(UiTextService.LanguageCookieName, out var languageCode);

        var cultureName = UiTextService.NormalizeLanguageCode(languageCode) switch
        {
            "nb" => "nb-NO",
            "sv" => "sv-SE",
            "da" => "da-DK",
            "fi" => "fi-FI",
            "nl" => "nl-NL",
            "fr" => "fr-FR",
            _ => "en-GB"
        };

        return Task.FromResult<ProviderCultureResult?>(
            new ProviderCultureResult(cultureName, cultureName));
    }));

app.UseRequestLocalization(requestLocalizationOptions);
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();