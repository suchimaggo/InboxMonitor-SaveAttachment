const statusBlock = document.querySelector(".status-block");
const statusLed = document.querySelector("#status-led");
const statusLabel = document.querySelector("#status-label");
const statusDetail = document.querySelector("#status-detail");
const startButton = document.querySelector("#start-button");
const stopButton = document.querySelector("#stop-button");
const activityList = document.querySelector("#activity-list");
const connectionState = document.querySelector("#connection-state");
let renderedActivity = "";

function setConnectionError(message) {
  statusBlock.dataset.state = "error";
  statusLabel.textContent = "Service unavailable";
  statusDetail.textContent = message;
  connectionState.textContent = "Not connected";
  startButton.disabled = true;
  stopButton.disabled = true;
}

function renderActivity(entries) {
  const latestKey = entries.map(entry => `${entry.timestamp}:${entry.message}`).join("\n");
  if (latestKey === renderedActivity)
    return;
  renderedActivity = latestKey;
  activityList.replaceChildren();

  if (entries.length === 0) {
    const empty = document.createElement("li");
    empty.className = "empty-state";
    empty.textContent = "Waiting for monitor activity.";
    activityList.append(empty);
    return;
  }

  for (const entry of entries.slice(-80)) {
    const item = document.createElement("li");
    item.className = "activity-item";

    const time = document.createElement("time");
    time.className = "activity-time";
    time.dateTime = entry.timestamp;
    time.textContent = new Date(entry.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });

    const message = document.createElement("p");
    message.className = "activity-message";
    message.textContent = entry.message;
    item.append(time, message);
    activityList.append(item);
  }

  activityList.scrollTop = activityList.scrollHeight;
}

function renderStatus(status) {
  const running = status.isRunning;
  statusBlock.dataset.state = running ? "running" : "stopped";
  statusLabel.textContent = running ? "Monitoring" : "Stopped";
  statusDetail.textContent = running ? "Checking your inbox" : "No inbox checks running";
  document.querySelector("#control-title").textContent = running ? "Inbox checks are active" : "Ready when you are";
  document.querySelector("#provider-value").textContent = status.provider;
  document.querySelector("#email-value").textContent = status.email || "Not configured";
  document.querySelector("#filter-value").textContent = status.subjectFilter;
  document.querySelector("#interval-value").textContent = `${status.pollIntervalSeconds} seconds`;
  document.querySelector("#folder-value").textContent = status.outputFolder;
  document.querySelector("#feed-count").textContent = `${status.activity.length} EVENTS`;
  connectionState.textContent = "Connected to local service";
  startButton.disabled = running;
  stopButton.disabled = !running;
  renderActivity(status.activity);
}

async function refreshStatus() {
  try {
    const response = await fetch("/api/status", { cache: "no-store" });
    if (!response.ok)
      throw new Error(`Status request failed (${response.status})`);
    renderStatus(await response.json());
  } catch (error) {
    setConnectionError(error instanceof Error ? error.message : "Unable to connect");
  }
}

async function sendAction(action) {
  startButton.disabled = true;
  stopButton.disabled = true;
  try {
    const response = await fetch(`/api/${action}`, { method: "POST" });
    if (!response.ok)
      throw new Error(`Request failed (${response.status})`);
    renderStatus(await response.json());
  } catch (error) {
    setConnectionError(error instanceof Error ? error.message : "Request failed");
  }
}

startButton.addEventListener("click", () => sendAction("start"));
stopButton.addEventListener("click", () => sendAction("stop"));
refreshStatus();
window.setInterval(refreshStatus, 2500);