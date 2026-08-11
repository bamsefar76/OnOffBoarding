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
    public EditModel Edit { get; set; } = new() { Active = true, SortOrder = 100, FulfillmentType = "Manual" };

    [TempData]
    public string? StatusMessageKey { get; set; }

    [TempData]
    public string? StatusMessageArgument { get; set; }

    public string? ErrorMessageKey { get; private set; }
    public List<ProductRow> Products { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnGetNewAsync()
    {
        SelectedId = null;
        Edit = new EditModel { Active = true, SortOrder = 100, FulfillmentType = "Manual" };
        await LoadPageAsync(loadSelected: false);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Normalize(Edit);

        ErrorMessageKey = Validate(Edit);
        if (!string.IsNullOrWhiteSpace(ErrorMessageKey))
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
    FulfillmentType = @FulfillmentType,
    AdGroupName = @AdGroupName,
    LicenseCount = @LicenseCount,
    Active = @Active,
    SortOrder = @SortOrder,
    UpdatedAt = SYSDATETIME()
WHERE LicenseProductId = @Id;";

                if (await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted) == 0)
                {
                    ErrorMessageKey = "licenseProducts.error.notFound";
                    await LoadPageAsync(cn, loadSelected: false);
                    return Page();
                }

                SelectedId = Edit.LicenseProductId;
                StatusMessageKey = "licenseProducts.message.saved";
                StatusMessageArgument = Edit.Name;
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
    FulfillmentType,
    AdGroupName,
    LicenseCount,
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
    @FulfillmentType,
    @AdGroupName,
    @LicenseCount,
    @Active,
    @SortOrder,
    SYSDATETIME(),
    SYSDATETIME()
);";

                SelectedId = Convert.ToInt32(
                    await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
                StatusMessageKey = "licenseProducts.message.created";
                StatusMessageArgument = Edit.Name;
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessageKey = "licenseProducts.error.duplicate";
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
        StatusMessageKey = active
            ? "licenseProducts.message.activated"
            : "licenseProducts.message.disabled";

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
            cmd.Parameters.AddNVarChar(
                "@Search",
                string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
                200);
            cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
            cmd.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel,
    FulfillmentType,
    AdGroupName,
    LicenseCount,
    Active,
    SortOrder,
    CreatedAt,
    UpdatedAt,
    (
        SELECT COUNT(*)
        FROM dbo.LicenseAssignments AS assignment
        WHERE assignment.LicenseProductId = dbo.LicenseProducts.LicenseProductId
          AND assignment.Status = N'Active'
          AND assignment.StartDate <= CAST(SYSDATETIME() AS date)
          AND (assignment.IsPermanent = 1 OR assignment.EndDate IS NULL OR assignment.EndDate >= CAST(SYSDATETIME() AS date))
    ) AS CurrentInUse
FROM dbo.LicenseProducts
WHERE (@ShowInactive = 1 OR Active = 1)
  AND
  (
      @Search IS NULL
      OR Name LIKE N'%' + @Search + N'%'
      OR Description LIKE N'%' + @Search + N'%'
      OR ProductFamily LIKE N'%' + @Search + N'%'
      OR AdGroupName LIKE N'%' + @Search + N'%'
  )
ORDER BY SortOrder, COALESCE(ProductFamily, Name), LicenseLevel, Name;";

            await using var reader =
                await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);

            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                Products.Add(new ProductRow
                {
                    LicenseProductId = reader.GetInt32(0),
                    Name = GetString(reader, 1),
                    Description = GetString(reader, 2),
                    ProductFamily = GetString(reader, 3),
                    LicenseLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    FulfillmentType = GetString(reader, 5),
                    AdGroupName = GetString(reader, 6),
                    LicenseCount = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Active = reader.GetBoolean(8),
                    SortOrder = reader.GetInt32(9),
                    CreatedAt = reader.GetDateTime(10),
                    UpdatedAt = reader.GetDateTime(11),
                    CurrentInUse = reader.GetInt32(12)
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
    FulfillmentType,
    AdGroupName,
    LicenseCount,
    Active,
    SortOrder
FROM dbo.LicenseProducts
WHERE LicenseProductId = @Id;";

            await using var reader =
                await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);

            if (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                Edit = new EditModel
                {
                    LicenseProductId = reader.GetInt32(0),
                    Name = GetString(reader, 1),
                    Description = GetString(reader, 2),
                    ProductFamily = GetString(reader, 3),
                    LicenseLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    FulfillmentType = GetString(reader, 5),
                    AdGroupName = GetString(reader, 6),
                    LicenseCount = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Active = reader.GetBoolean(8),
                    SortOrder = reader.GetInt32(9)
                };
            }
        }
    }

    private static string? Validate(EditModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return "licenseProducts.validation.nameRequired";

        if (model.Name.Length > 200)
            return "licenseProducts.validation.nameTooLong";

        if (model.Description?.Length > 1000)
            return "licenseProducts.validation.descriptionTooLong";

        if (model.ProductFamily?.Length > 100)
            return "licenseProducts.validation.familyTooLong";

        if (model.FulfillmentType is not ("Manual" or "AdGroup"))
            return "licenseProducts.validation.fulfillmentInvalid";

        if (model.FulfillmentType == "AdGroup" && string.IsNullOrWhiteSpace(model.AdGroupName))
            return "licenseProducts.validation.adGroupRequired";

        if (model.AdGroupName?.Length > 300)
            return "licenseProducts.validation.adGroupTooLong";

        if (model.LicenseCount.HasValue && model.LicenseCount.Value < 0)
            return "licenseProducts.validation.licenseCountNegative";

        return null;
    }

    private static void Normalize(EditModel model)
    {
        model.Name = model.Name?.Trim() ?? "";
        model.Description = NullIfWhiteSpace(model.Description);
        model.ProductFamily = NullIfWhiteSpace(model.ProductFamily);
        var fulfillmentType = model.FulfillmentType?.Trim() ?? "";
        model.FulfillmentType = string.Equals(fulfillmentType, "AdGroup", StringComparison.OrdinalIgnoreCase)
            ? "AdGroup"
            : string.Equals(fulfillmentType, "Manual", StringComparison.OrdinalIgnoreCase)
                ? "Manual"
                : fulfillmentType;
        model.AdGroupName = model.FulfillmentType == "AdGroup"
            ? NullIfWhiteSpace(model.AdGroupName)
            : null;
    }

    private static void AddParameters(SqlCommand cmd, EditModel model)
    {
        cmd.Parameters.AddRequiredNVarChar("@Name", model.Name, 200);
        cmd.Parameters.AddNVarChar("@Description", model.Description, 1000);
        cmd.Parameters.AddNVarChar("@ProductFamily", model.ProductFamily, 100);
        cmd.Parameters.AddNullableInt("@LicenseLevel", model.LicenseLevel);
        cmd.Parameters.AddRequiredNVarChar("@FulfillmentType", model.FulfillmentType, 20);
        cmd.Parameters.AddNVarChar("@AdGroupName", model.AdGroupName, 300);
        cmd.Parameters.AddNullableInt("@LicenseCount", model.LicenseCount);
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
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? ProductFamily { get; set; }
        public int? LicenseLevel { get; set; }
        public string FulfillmentType { get; set; } = "Manual";
        public string? AdGroupName { get; set; }
        public int? LicenseCount { get; set; }
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
        public string FulfillmentType { get; init; } = "Manual";
        public string AdGroupName { get; init; } = "";
        public int? LicenseCount { get; init; }
        public int CurrentInUse { get; init; }
        public bool Active { get; init; }
        public int SortOrder { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
