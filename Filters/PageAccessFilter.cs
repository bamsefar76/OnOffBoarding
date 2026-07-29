using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Filters;

public class PageAccessFilter : IAsyncPageFilter
{
    private readonly PageAccessService _pageAccessService;
    private readonly AccessScopeService _accessScopeService;

    public PageAccessFilter(
        PageAccessService pageAccessService,
        AccessScopeService accessScopeService)
    {
        _pageAccessService = pageAccessService;
        _accessScopeService = accessScopeService;
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        var userName = context.HttpContext.User.Identity?.Name ?? "";
        var pagePath = context.ActionDescriptor.ViewEnginePath;

        var hasAccess = await _pageAccessService.UserHasAccessAsync(userName, pagePath);

        if (!hasAccess)
        {
            hasAccess = await _accessScopeService.CanOpenScopedPageAsync(
                context.HttpContext.User,
                pagePath,
                context.HttpContext.RequestAborted);
        }

        if (!hasAccess)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}
