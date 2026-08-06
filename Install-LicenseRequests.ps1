param([string]$ProjectRoot = ".")

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path $ProjectRoot).Path
$PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$files = @(
    "Services\LicenseEmailService.cs",
    "Pages\LicenseRequests\Index.cshtml",
    "Pages\LicenseRequests\Index.cshtml.cs"
)

foreach ($file in $files)
{
    $source = Join-Path $PackageRoot $file
    $target = Join-Path $ProjectRoot $file

    New-Item -ItemType Directory `
        -Path (Split-Path $target -Parent) `
        -Force | Out-Null

    Copy-Item -LiteralPath $source -Destination $target -Force
    Write-Host "Copied $file"
}

$programPath = Join-Path $ProjectRoot "Program.cs"
$program = Get-Content -LiteralPath $programPath -Raw

if ($program -notmatch "AddScoped<LicenseEmailService>")
{
    $needle = "builder.Services.AddScoped<SqlConnectionFactory>();"

    if (-not $program.Contains($needle))
    {
        throw "Could not find SqlConnectionFactory registration in Program.cs."
    }

    $program = $program.Replace(
        $needle,
        $needle + [Environment]::NewLine +
        "builder.Services.AddScoped<LicenseEmailService>();"
    )

    Set-Content -LiteralPath $programPath `
        -Value $program `
        -Encoding utf8

    Write-Host "Registered LicenseEmailService"
}

Write-Host ""
Write-Host "Run Database\LicenseRequests.Web.Required.sql in SSMS."
Write-Host "Then run: dotnet clean; dotnet run"
Write-Host "Open: /LicenseRequests"
