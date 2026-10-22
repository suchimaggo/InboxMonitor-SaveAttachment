# Install Inbox Utilities on Windows

The ZIP contains the Utility Dashboard, Inbox Reader, New Loan Case, Loan Processing, and Loan Application Completeness Checker as self-contained Windows x64 applications. It does not require the .NET SDK or .NET runtime. It does not include mailbox or Zoho OAuth credentials. Persistent application data is stored on `Z:\LoanProcessing-AutoMation`; the Z: drive must be connected and writable before startup.

## 1. Copy the package to the new laptop

Copy `InboxUtilities-win-x64.zip` to the laptop and extract it to a folder. Right-click the ZIP and select **Extract All**. Do not run the application from inside the ZIP.

## 2. Install OCR support

Install Tesseract for Windows to use OCR in Loan Processing. The Loan Processing project recommends the [UB Mannheim Windows installer](https://github.com/UB-Mannheim/tesseract/wiki); keep its default installation folder if possible. Restart Windows after installation if the installer requests it.

ImageMagick is optional. Install it if scanned/image-only PDFs need OCR; PDFs with embedded text and image files do not need ImageMagick.

## 3. Install the applications

Open the extracted package folder, right-click `Install.ps1`, and choose **Run with PowerShell**. If Windows blocks the script, open PowerShell in the extracted folder and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

The default install location is `%LOCALAPPDATA%\Programs\InboxUtilities`. No administrator rights are needed. The installer creates **Inbox Utilities** shortcuts on the Desktop and in the Start Menu.

The startup script now creates the shared `Z:\LoanProcessing-AutoMation` folders if needed and moves any existing local case, inbox attachment, and checker data into the shared drive so the data is centralized for all utilities.

## 4. Configure Inbox Reader

Before starting the utilities, open:

```text
%LOCALAPPDATA%\Programs\InboxUtilities\InboxReader\appsettings.json
```

Set `MailExchangeServer` to `Zoho` or `Outlook` to choose the mailbox service. This setting takes precedence over the legacy `Provider` setting, which remains supported if `MailExchangeServer` is omitted. For Zoho, configure `Email`, an app-specific `Password`, and optionally `ImapHost` for your Zoho data center. For Microsoft 365/Exchange Online, configure `Email`, `OutlookClientId`, and (for organizational accounts) the appropriate `OutlookTenantId`; Outlook uses `outlook.office365.com` and OAuth device-code sign-in, and ignores the Zoho-specific `ImapHost`. The Azure app registration needs the delegated IMAP permission and consent, and IMAP must be enabled for the mailbox. Inbox Reader only searches messages delivered on or after 15 October 2026 (inclusive), configured by `ScanFromDate`. It checks subjects for a saved case reference in the format `Client Name-CASE-ID`; leave `SubjectFilter` blank. Keep that reference in follow-up email subjects so attachments are filed under the right case. Unmatched email attachments are stored under `Z:\LoanProcessing-AutoMation\InboxAttachments`. New cases are stored under `Z:\LoanProcessing-AutoMation\Clients\<CLIENT-NAME>\<CASE-ID>`, for example `Clients\SHYAM-MAGGO\REFI-3Q8N5W`, with matched email attachments under that case's `Received` folder. The client folder is uppercase with spaces replaced by hyphens; each unique case ID gets a separate folder. New Loan Case and Completeness Checker use the same loan type catalogue at `Z:\LoanProcessing-AutoMation\loan-types.json`; do not change its IDs or checklist scenario mappings when changing display names. The launcher sets the shared root; keep `CaseRootFolder` and `TrackingRefreshUrl` unchanged. For Zoho, use an app-specific password rather than your regular account password. For Outlook, configure `OutlookClientId` and follow the existing Outlook app-registration setup.

The password in this local settings file is stored as plain text. Keep the file private and do not send or commit it. The published ZIP contains a blank password. If Z: is disconnected or not writable, startup stops rather than saving case data or attachments locally.

## 5. Configure New Loan Case (optional Zoho drafts)

New Loan Case is included in the Dashboard and starts with the other local services. Case creation and storage work without Zoho credentials; creating email drafts requires OAuth configuration. Set these Windows user environment variables to the values from your Zoho OAuth setup:

```text
Zoho__ClientId
Zoho__ClientSecret
Zoho__RefreshToken
Zoho__AccountId
Zoho__FromAddress
```

The utility defaults to Zoho's `zoho.com` endpoints. If your Zoho account uses another data center, also set `Zoho__TokenUrl` and `Zoho__ApiBaseUrl` to that region's HTTPS endpoints. The startup script loads these values from your Windows user environment into the application processes each time they start, so credentials saved after sign-in or after PowerShell was opened are picked up. It warns if any required draft-credential variable is unavailable. Restart the applications after changing environment variables. The OAuth scope and setup notes are in `NewCase\README.md` in the install folder. Keep secrets private; do not put them in shared files or source control.

## 6. Start and stop

Use the **Inbox Utilities** Desktop or Start Menu shortcut. It starts Inbox Reader, the Completeness Checker, New Loan Case, and the Dashboard, then opens. It moves existing local cases, saved inbox attachments, and checker template data to the shared drive and removes the local copies after a successful move; Z: files are kept if they already exist:

<http://127.0.0.1:5086>

Select **New Loan Case** to create a loan case and Zoho draft. Ask the client to include the generated case ID in every follow-up email subject. The Dashboard checks received attachments against that case's requested-document list after Inbox Reader polls. Filename/content matches are suggestions and require staff review; unreadable or unsupported files are identified, not silently treated as received. No automatic client reminders are sent. Select **Loan Processing** to organize files; confirm before processing because it can move and rename them.

To stop the web applications, use the **Stop Inbox Utilities** shortcut in the Start Menu. Complete any active Loan Processing job before stopping the apps.

## Troubleshooting

- If the Z: drive is unavailable, connect it and confirm `Z:\LoanProcessing-AutoMation` is writable, then start again.
- If a port is already in use, close the other local service using port `5086`, `5087`, `5088`, or `5089`.
- If Inbox Reader does not start, check `InboxReader\appsettings.json` and the logs in the install folder's `logs` directory.
- If OCR is unavailable, confirm `CompletenessChecker\tessdata\eng.traineddata` and `LoanProcessing\tessdata\eng.traineddata` exist. For PDF-to-image OCR fallback, install ImageMagick.
- This package targets Windows x64. It is not an ARM64 package.
