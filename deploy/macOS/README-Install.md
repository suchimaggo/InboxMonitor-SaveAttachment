# Install Inbox Utilities on Apple silicon

This is a separate self-contained .NET package for Apple silicon Macs (`osx-arm64`, including M1–M4). The macOS laptop does not need the .NET SDK/runtime. It includes the Dashboard, Inbox Reader, New Loan Case, and Loan Processing. The package includes the English Tesseract language data but requires the Tesseract command-line program for OCR.

## 1. Extract the package

Copy `InboxUtilities-macOS-arm64.zip` to the Mac and double-click it in Finder. Do not run the applications from inside the ZIP.

## 2. Install OCR tools

Install Homebrew if needed, then open Terminal and install Tesseract:

```sh
brew install tesseract
```

ImageMagick is optional. Install it only if you need OCR fallback for scanned PDFs:

```sh
brew install imagemagick
```

PDFs with embedded text and image files do not need ImageMagick.

## 3. Install the applications

Open Terminal, change into the extracted `InboxUtilities` folder, and run:

```sh
bash ./Install.command
```

The installer copies the apps into `~/Applications/InboxUtilities`. No administrator password is needed.

## 4. Configure Inbox Reader

Edit:

```text
~/Applications/InboxUtilities/InboxReader/appsettings.json
```

Set `Provider`, `Email`, `Password`, `ImapHost`, and (if desired) `OutputFolder`. Inbox Reader only searches messages delivered on or after 15 October 2026 (inclusive), configured by `ScanFromDate`. It matches case emails using the `Client Name-CASE-ID` subject format; keep this reference in replies. Unmatched attachments are stored in the configured output folder. For Zoho, use an app-specific password, not your normal account password. The start script sets the default output folder to `~/InboxAttachments`. For Outlook, configure `OutlookClientId` and follow your Outlook app-registration setup.

The password in this local file is plain text. Keep the file private. The ZIP is published with an empty email and password.

## 5. Configure New Loan Case (optional Zoho drafts)

New Loan Case starts with the other local services. Case creation and storage work without Zoho credentials; creating email drafts requires OAuth configuration. In the same Terminal session you will use to start the apps, export these values:

```sh
export Zoho__ClientId="YOUR_CLIENT_ID"
export Zoho__ClientSecret="YOUR_CLIENT_SECRET"
export Zoho__RefreshToken="YOUR_REFRESH_TOKEN"
export Zoho__AccountId="YOUR_ZOHO_MAIL_ACCOUNT_ID"
export Zoho__FromAddress="your-mailbox@example.com"
```

Then start the apps from the same session with `bash ~/Applications/InboxUtilities/Start.command`. The utility defaults to Zoho's `zoho.com` endpoints. For another data center, also export `Zoho__TokenUrl` and `Zoho__ApiBaseUrl` using that region's HTTPS endpoints. Restart the utilities after changing environment variables. The OAuth scope and setup notes are in `~/Applications/InboxUtilities/NewCase/README.md`. Keep secrets private.

## 6. Start and stop

In Terminal, run `bash ~/Applications/InboxUtilities/Start.command`. The dashboard opens at <http://127.0.0.1:5086>. Select New Loan Case to create a loan case and Zoho draft. Select Loan Processing to organize files; confirm before processing because it can move and rename them. The Loan Application Completeness Checker is developed and run separately from the Dashboard.

Run `bash ~/Applications/InboxUtilities/Stop.command` to stop the Dashboard and Inbox Reader. Finish any active Loan Processing run first.

## Notes and troubleshooting

- This package is for Apple silicon. Intel Macs require a separate `osx-x64` publish.
- If a port is already in use, stop the other local app using ports `5086`, `5087`, or `5088`.
- Logs are in `~/Applications/InboxUtilities/logs`.
- If OCR cannot find Tesseract, ensure Homebrew's `bin` directory is on `PATH` (normally `/opt/homebrew/bin` on Apple silicon). The included `tessdata/eng.traineddata` is used by the OCR command.
- If macOS reports that the app is damaged, use the included command scripts from the extracted folder and check that the ZIP was fully downloaded and extracted.
- The binaries are unsigned. If Gatekeeper blocks a binary you built and trust, remove quarantine only from the installed utility folder with `xattr -dr com.apple.quarantine ~/Applications/InboxUtilities`, then try again.
