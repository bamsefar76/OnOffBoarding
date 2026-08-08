using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

[Authorize]
public sealed class LicenseProductsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public LicenseProductsModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int? SelectedId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowInactive { get; set; }

    [BindProperty]
    public EditModel Edit { get; set; } = new() { Active = true, SortOrder = 100 };

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }
    public List<ProductRow> Products { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnGetNewAsync()
    {
        SelectedId = null;
        Edit = new EditModel { Active = true, SortOrder = 100 };
        await LoadPageAsync(loadSelected: false);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Normalize(Edit);

        if (!ModelState.IsValid)
        {
            SelectedId = Edit.LicenseProductId > 0 ? Edit.LicenseProductId : null;
            await LoadPageAsync(loadSelected: false);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        try
        {
            if (Edit.LicenseProductId > 0)
            {
                await using var cmd = cn.CreateCommand();
                AddParameters(cmd, Edit);
                cmd.Parameters.AddInt("@Id", Edit.LicenseProductId);
                cmd.CommandText = @"
UPDATE dbo.LicenseProducts
SET
    Name = @Name,
    Description = @Description,
    ProductFamily = @ProductFamily,
    LicenseLevel = @LicenseLevel,
    Active = @Active,
    SortOrder = @SortOrder,
    UpdatedAt = SYSDATETIME()
WHERE LicenseProductId = @Id;";

                if (await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted) == 0)
                {
                    ErrorMessage = "The license product was not found.";
                    await LoadPageAsync(cn, loadSelected: false);
                    return Page();
                }

                SelectedId = Edit.LicenseProductId;
                StatusMessage = $"Saved license product '{Edit.Name}'.";
            }
            else
            {
                await using var cmd = cn.CreateCommand();
                AddParameters(cmd, Edit);
                cmd.CommandText = @"
INSERT INTO dbo.LicenseProducts
(
    Name,
    Description,
    ProductFamily,
    LicenseLevel,
    Active,
    SortOrder,
    CreatedAt,
    UpdatedAt
)
OUTPUT INSERTED.LicenseProductId
VALUES
(
    @Name,
    @Description,
    @ProductFamily,
    @LicenseLevel,
    @Active,
    @SortOrder,
    SYSDATETIME(),
    SYSDATETIME()
);";

                SelectedId = Convert.ToInt32(
                    await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
                StatusMessage = $"Created license product '{Edit.Name}'.";
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessage = "A license product with the same unique value already exists.";
            await LoadPageAsync(cn, loadSelected: false);
            return Page();
        }

        return RedirectToPage(new { id = SelectedId, Search, ShowInactive });
    }

    public async Task<IActionResult> OnPostSetActiveAsync(int id, bool active)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@Id", id);
        cmd.Parameters.AddBit("@Active", active);
        cmd.CommandText = @"
UPDATE dbo.LicenseProducts
SET
    Active = @Active,
    UpdatedAt = SYSDATETIME()
WHERE LicenseProductId = @Id;";

        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = active
            ? "License product was activated."
            : "License product was disabled.";

        return RedirectToPage(new { id, Search, ShowInactive });
    }

    private async Task LoadPageAsync(bool loadSelected = true)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await LoadPageAsync(cn, loadSelected);
    }

    private async Task LoadPageAsync(SqlConnection cn, bool loadSelected = true)
    {
        Products.Clear();

        await using (var cmd = cn.CreateCommand())
        {
            cmd.Parameters.AddNVarChar("@Search", string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(), 200);
            cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
            cmd.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel,
    Active,
    SortOrder,
    CreatedAt,
    UpdatedAt
FROM dbo.LicenseProducts
WHERE (@ShowInactive = 1 OR Active = 1)
  AND
  (
      @Search IS NULL
      OR Name LIKE N'%' + @Search + N'%'
      OR Description LIKE N'%' + @Search + N'%'
      OR ProductFamily LIKE N'%' + @Search + N'%'
  )
ORDER BY SortOrder, COALESCE(ProductFamily, Name), LicenseLevel, Name;";

            await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                Products.Add(new ProductRow
                {
                    LicenseProductId = reader.GetInt32(0),
                    Name = GetString(reader, 1),
                    Description = GetString(reader, 2),
                    ProductFamily = GetString(reader, 3),
                    LicenseLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Active = reader.GetBoolean(5),
                    SortOrder = reader.GetInt32(6),
                    CreatedAt = reader.GetDateTime(7),
                    UpdatedAt = reader.GetDateTime(8)
                });
            }
        }

        if (loadSelected && SelectedId.HasValue)
        {
            await using var cmd = cn.CreateCommand();
            cmd.Parameters.AddInt("@Id", SelectedId.Value);
            cmd.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel,
    Active,
    SortOrder
FROM dbo.LicenseProducts
WHERE LicenseProductId = @Id;";

            await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            if (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                Edit = new EditModel
                {
                    LicenseProductId = reader.GetInt32(0),
                    Name = GetString(reader, 1),
                    Description = GetString(reader, 2),
                    ProductFamily = GetString(reader, 3),
                    LicenseLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Active = reader.GetBoolean(5),
                    SortOrder = reader.GetInt32(6)
                };
            }
        }
    }

    private static void Normalize(EditModel model)
    {
        model.Name = model.Name?.Trim() ?? "";
        model.Description = NullIfWhiteSpace(model.Description);
        model.ProductFamily = NullIfWhiteSpace(model.ProductFamily);
    }

    private static void AddParameters(SqlCommand cmd, EditModel model)
    {
        cmd.Parameters.AddRequiredNVarChar("@Name", model.Name, 200);
        cmd.Parameters.AddNVarChar("@Description", model.Description, 1000);
        cmd.Parameters.AddNVarChar("@ProductFamily", model.ProductFamily, 100);
        cmd.Parameters.AddNullableInt("@LicenseLevel", model.LicenseLevel);
        cmd.Parameters.AddBit("@Active", model.Active);
        cmd.Parameters.AddInt("@SortOrder", model.SortOrder);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    public sealed class EditModel
    {
        public int LicenseProductId { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(1000)]
        public string? Description { get; set; }

        [StringLength(100)]
        public string? ProductFamily { get; set; }

        public int? LicenseLevel { get; set; }
        public bool Active { get; set; } = true;
        public int SortOrder { get; set; } = 100;
    }

    public sealed class ProductRow
    {
        public int LicenseProductId { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string ProductFamily { get; init; } = "";
        public int? LicenseLevel { get; init; }
        public bool Active { get; init; }
        public int SortOrder { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
