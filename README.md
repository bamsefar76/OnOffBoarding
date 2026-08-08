# Database-backed public portal URL

The license request email now reads the externally reachable base URL from:

    dbo.ApplicationSettings
    SettingKey = PublicBaseUrl

Run:

    Database\ApplicationSettings.PublicBaseUrl.sql

Then set the real value:

```sql
UPDATE dbo.ApplicationSettings
SET
    SettingValue = N'https://YOUR-REAL-PORTAL-HOSTNAME',
    Active = 1,
    LastUpdated = SYSDATETIME()
WHERE SettingKey = N'PublicBaseUrl';
```

Do not append `/LicenseRequests`.

Replace:

    Pages\LicenseRequests\Index.cshtml.cs

No environment variable or Program.cs change is required.
