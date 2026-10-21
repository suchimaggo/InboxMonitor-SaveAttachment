const form = document.querySelector("#process-form");
const folderPath = document.querySelector("#folder-path");
const startButton = document.querySelector("#start-button");
const runStatus = document.querySelector("#run-status");
const statusDetail = document.querySelector("#status-detail");
const activityLog = document.querySelector("#activity-log");
const activityCount = document.querySelector("#activity-count");
const pageError = document.querySelector("#page-error");

function setError(message) {
  pageError.textContent = message;
  pageError.hidden = !message;
}

function renderStatus(status) {
  runStatus.textContent = status.isRunning
    ? "Processing"
    : status.error
      ? "Needs attention"
      : status.exitCode === 0
        ? "Complete"
        : status.isConfigured
          ? "Ready"
          : "Unavailable";
  statusDetail.textContent = status.isRunning
    ? status.folderPath
    : status.error
      ? status.error
      : status.isConfigured
        ? status.folderPath || "Choose a folder to begin."
        : "The configured Loan Processing project was not found.";
  startButton.disabled = status.isRunning || !status.isConfigured;

  const entries = status.activity ?? [];
  activityLog.textContent = entries.length ? entries.join("\n") : "No processing activity yet.";
  activityCount.textContent = `${entries.length} ${entries.length === 1 ? "line" : "lines"}`;
  activityLog.scrollTop = activityLog.scrollHeight;
}

async function refreshStatus() {
  try {
    const response = await fetch("/api/loan-processing/status", { cache: "no-store" });
    if (!response.ok)
      throw new Error(`Status request failed (${response.status})`);
    renderStatus(await response.json());
    setError("");
  } catch (error) {
    setError(error instanceof Error ? error.message : "Unable to read processor status.");
    runStatus.textContent = "Unavailable";
    statusDetail.textContent = "Could not connect to the local processor.";
    startButton.disabled = true;
  }
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  const path = folderPath.value.trim();
  if (!path) {
    setError("Enter a folder path to continue.");
    folderPath.focus();
    return;
  }

  if (!window.confirm(`Loan Processing can move and rename files in:\n\n${path}\n\nContinue only if you have reviewed the folder and have a backup.`))
    return;

  setError("");
  startButton.disabled = true;
  try {
    const response = await fetch("/api/loan-processing/start", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ folderPath: path })
    });
    if (response.status === 409) {
      setError("Processing is already in progress. Wait for it to finish before starting another folder.");
    } else if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error ?? body?.detail ?? `Could not start processing (${response.status}).`);
    }
    await refreshStatus();
  } catch (error) {
    setError(error instanceof Error ? error.message : "Could not start processing.");
  } finally {
    startButton.disabled = false;
    await refreshStatus();
  }
});

refreshStatus();
window.setInterval(refreshStatus, 1500);
