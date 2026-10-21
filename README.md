# Inbox Utilities

This solution contains the Dashboard and the utilities the team will use first:

- **Utility Dashboard** — the launchpad at <http://127.0.0.1:5086>
- **Inbox Reader** — the inbox attachment utility at <http://127.0.0.1:5087>
- **New Loan Case** — create a case and prepare a Zoho email draft at <http://127.0.0.1:5088>
- **Loan Processing** — the document utility at <http://127.0.0.1:5086/loan/>, launched from the dashboard
- **Loan Application Completeness Checker** — text/OCR-assisted document checklist candidate scanning at <http://127.0.0.1:5089>

## Run the web apps

Start the dashboard and Inbox Reader in separate terminals from the repository root:

```powershell
dotnet run --project UtilityDashboard\UtilityDashboard.csproj
```

```powershell
dotnet run --project InboxReader.csproj
```

Start New Loan Case in another terminal:

```powershell
dotnet run --project "C:\Utilities\New Case Create\New Case Create.csproj"
```

Open the dashboard URL in a browser. The apps bind to loopback only and run independently. The dashboard links to Inbox Reader and New Loan Case; the desktop start/stop scripts manage those apps and the Dashboard.

The dashboard starts Loan Processing as a background console process when you submit a folder. It uses the project configured by `LoanProcessing:ProjectPath` in `UtilityDashboard/appsettings.json`; the default is the sibling `LoanProcessing-Utility` folder under `C:\Utilities`. Loan Processing may move and rename files, so the page requires confirmation before processing. OCR prerequisites are listed in [LoanProcessing-Utility/README.md](../LoanProcessing-Utility/README.md).

New Loan Case saves each case's requested documents in `Z:\LoanProcessing-AutoMation\Clients\<CLIENT-NAME>\<CASE-ID>\case.json` and generates the Zoho draft subject as `Client Name-CASE-ID`. For example, Shyam Maggo's cases have separate folders such as `Clients\SHYAM-MAGGO\REFI-3Q8N5W` and `Clients\SHYAM-MAGGO\PUR-7K4M2P`. Inbox Reader searches only messages delivered on or after 15 October 2026 (inclusive), checks inbox subjects for exactly one saved client-and-case reference, stores matching attachments under that case's `Received` folder, and saves unmatched attachments under `Z:\LoanProcessing-AutoMation\InboxAttachments`. The Dashboard refreshes checklist candidates after Inbox Reader polls, using the Completeness Checker for text extraction and OCR. Matches remain suggestions for staff review; only PDFs, JPEG/PNG images, and text files are inspected, and no client reminders are sent.

## Publish for a new Windows x64 laptop

From the repository root, run:

```powershell
.\publish-win-x64.ps1
```

This creates `artifacts\InboxUtilities-win-x64.zip`, a self-contained package with the Dashboard, Inbox Reader, New Loan Case, Loan Processing, and Completeness Checker. It excludes the development mailbox settings and replaces them with a blank-password template. Copy and extract the ZIP on the new laptop, then follow [deploy/README-Install.md](./deploy/README-Install.md). ImageMagick is optional for scanned-PDF OCR fallback.

## Publish for an Apple silicon Mac

From the repository root, run:

```powershell
.\publish-macos-arm64.ps1
```

This creates `artifacts\InboxUtilities-macOS-arm64.zip`, a separate self-contained package for Apple silicon (`osx-arm64`) with the Dashboard, Inbox Reader, New Loan Case, and Loan Processing. Extract it on the Mac and follow [deploy/macOS/README-Install.md](./deploy/macOS/README-Install.md). The Mac needs the Tesseract command-line application installed for OCR; ImageMagick is optional for scanned-PDF OCR fallback. The macOS package is cross-published on Windows and still needs runtime verification on an Apple silicon Mac.

## Add a utility

Add another entry to the `Utilities` array in [UtilityDashboard/appsettings.json](./UtilityDashboard/appsettings.json):

```json
{
  "Name": "My Utility",
  "Description": "A short description of the utility.",
  "Url": "http://127.0.0.1:5090/",
  "HealthUrl": "http://127.0.0.1:5090/health"
}
```

`HealthUrl` is optional. When omitted, the dashboard shows that the utility's status has not been configured.
