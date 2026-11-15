# Inbox Utilities

The utilities are designed to run as three separate Azure App Services. Staff sign in with Microsoft Entra ID; the services use their App Service Managed Identities for Microsoft Graph. SharePoint is the source of truth for cases and documents. The hosted workflow does not use a staff desktop or the `Z:` drive.

| App Service | Responsibilities |
|---|---|
| Dashboard | Utility links; reads SharePoint cases, scans `Received` files, and previews/applies reversible document organization within SharePoint. |
| New Case | Creates case folders and `Case Register` records in SharePoint; creates Outlook drafts without sending them. |
| Inbox Reader | Polls the configured Exchange Online mailbox through Microsoft Graph and routes attachments into SharePoint. |

The Dashboard also hosts the completeness-checking library in-process. Its scan files are downloaded into a unique temporary directory and removed after each scan.

## Azure setup

For each App Service:

1. Enable Microsoft Entra authentication (App Service Authentication/Easy Auth), require authentication, and use the organization's single-tenant app registration.
2. Enable a system-assigned Managed Identity (or configure `Azure__ManagedIdentityClientId` for a user-assigned identity).
3. Configure the Graph application permissions and SharePoint site grants described below. Do not put client secrets or mailbox passwords in app settings.
4. Configure the service settings as App Service application settings, using double underscores for nested keys.
5. Use the `/health` endpoint for App Service health checks. Keep the Inbox Reader service running continuously with Always On enabled.

The app's production middleware checks for the Easy Auth `X-MS-CLIENT-PRINCIPAL` header; this is an additional guard, not a replacement for enabling Easy Auth and requiring sign-in in Azure.

### SharePoint

Create a `Case Register` list with the internal column names `CaseID`, `ClientName`, `ClientEmail`, `CaseStatus`, `FolderName`, `FolderID`, and `CreatedUTC`. `CaseID` must be required, indexed, and unique. Configure an existing document-library parent folder for cases.

Set these settings for all three services:

```text
SharePoint__SiteId
SharePoint__ListId
SharePoint__DriveId
SharePoint__ParentFolderPath
```

The New Case service also accepts `SharePoint__TemplatesFilePath`. The Inbox Reader accepts `SharePoint__UnmatchedFolderPath`.

Grant each Managed Identity only the required site access. New Case needs SharePoint write access. Dashboard needs read access to the register and write access to case folders for document organization and run journals. Inbox Reader needs read access to the list and write access to case folders and the unmatched-attachments folder. `Sites.Selected` with explicit site grants is preferred.

### Mailbox access

Set `GraphMail__Mailbox` for New Case, and `InboxReader__Mailbox`, `InboxReader__ScanFromDate`, and `InboxReader__PollIntervalSeconds` for Inbox Reader. Grant New Case the Graph permission needed to create drafts (`Mail.ReadWrite`); grant Inbox Reader the permission needed to read messages and attachments. Restrict application mailbox access to the designated mailbox using Exchange Online Application RBAC or the organization's approved equivalent.

## Dashboard settings

Set the hosted URLs in `UtilityDashboard/appsettings.json` or override them with deployment configuration:

```text
Utilities__0__Url
Utilities__0__HealthUrl
Utilities__1__Url
Utilities__1__HealthUrl
```

Set the Dashboard's SharePoint settings and Managed Identity configuration in the same way as the other services. The Dashboard scans PDFs, images, and text files. For image OCR, deploy it on a platform with the required Tesseract runtime and English data; a Windows App Service is recommended unless those dependencies are packaged for Linux.

## Local development

Use the .NET 10 SDK for Inbox Reader. Local development uses `DefaultAzureCredential`; sign in with a developer identity that has equivalent Graph site and mailbox access. Configure SharePoint IDs and paths with .NET user-secrets or environment variables. Never commit production IDs, credentials, or client data.

```powershell
dotnet build InboxReader.csproj
dotnet build "C:\Utilities\SSB-Dashboard-with-3Tiles-Working-\New Case Create\New Case Create.csproj"
```

Build the Dashboard from the companion SSB Dashboard repository; the `UtilityDashboard` directory in this repository is a legacy desktop-package copy.

## Deployment status and migration notes

Source code changes do not provision Azure resources, configure Easy Auth, grant Graph access, or migrate historical records. Supply the actual SharePoint IDs, hosted service URLs, managed identities, and permission grants before deployment. Historical data is not automatically migrated; import only records and documents that must remain available, using an approved migration process.

Loan Processing is hosted within the Dashboard and operates only on SharePoint case files. Staff select a Case ID, review a temporary processing preview, and explicitly confirm moves. The Dashboard stores undo journals in SharePoint and verifies each file's hash and original-path availability before restoring it.

A mailbox password was previously present in the workspace configuration and has since been removed. Rotate it if it was valid; removing it from source does not revoke the credential.

The Windows and macOS installer guides and packaging scripts describe the retired desktop deployment model. Do not use them to deploy the hosted services.
