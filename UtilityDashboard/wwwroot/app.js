const grid = document.querySelector("#utility-grid");
const utilityCount = document.querySelector("#utility-count");
const totalCount = document.querySelector("#total-count");
const updatedAt = document.querySelector("#updated-at");
const loadError = document.querySelector("#load-error");
const availabilityLabels = {
  online: "Online",
  offline: "Unavailable",
  "not-configured": "Status not configured"
};

function createUtilityCard(utility, index) {
  const card = document.createElement("article");
  card.className = "utility-card";

  const cardTop = document.createElement("div");
  cardTop.className = "card-top";

  const icon = document.createElement("span");
  icon.className = "utility-icon";
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = utility.name === "Inbox Reader" ? "IR" : utility.name.slice(0, 2).toUpperCase();

  const status = document.createElement("span");
  status.className = `availability availability-${utility.availability}`;
  const statusDot = document.createElement("span");
  statusDot.className = "availability-dot";
  status.append(statusDot, document.createTextNode(availabilityLabels[utility.availability] ?? "Unknown"));
  cardTop.append(icon, status);

  const number = document.createElement("span");
  number.className = "card-number";
  number.textContent = String(index + 1).padStart(2, "0");

  const title = document.createElement("h3");
  title.textContent = utility.name;

  const description = document.createElement("p");
  description.className = "card-description";
  description.textContent = utility.description;

  const link = document.createElement("a");
  link.className = "launch-link";
  link.href = utility.url;
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.setAttribute("aria-label", `Open ${utility.name} in a new tab`);
  const linkLabel = document.createElement("span");
  linkLabel.textContent = "Open utility";
  const arrow = document.createElement("span");
  arrow.className = "launch-arrow";
  arrow.setAttribute("aria-hidden", "true");
  arrow.textContent = "↗";
  link.append(linkLabel, arrow);

  card.append(cardTop, number, title, description, link);
  return card;
}

function renderUtilities(utilities) {
  grid.replaceChildren();
  const available = utilities.filter(utility => utility.availability === "online").length;

  if (utilities.length === 0) {
    const empty = document.createElement("p");
    empty.className = "loading-message";
    empty.textContent = "No utilities configured yet. Add one in appsettings.json to get started.";
    grid.append(empty);
  } else {
    utilities.forEach((utility, index) => grid.append(createUtilityCard(utility, index)));
  }

  utilityCount.textContent = `${available} of ${utilities.length} online`;
  totalCount.textContent = String(utilities.length).padStart(2, "0");
  updatedAt.textContent = `Last checked ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
  grid.setAttribute("aria-busy", "false");
  loadError.hidden = true;
}

async function refreshUtilities() {
  try {
    const response = await fetch("/api/utilities", { cache: "no-store" });
    if (!response.ok)
      throw new Error(`Dashboard request failed (${response.status})`);
    renderUtilities(await response.json());
  } catch (error) {
    loadError.textContent = error instanceof Error ? error.message : "Unable to load utilities.";
    loadError.hidden = false;
    utilityCount.textContent = "Status unavailable";
    updatedAt.textContent = "Could not check local services";
    grid.setAttribute("aria-busy", "false");
  }
}

refreshUtilities();
window.setInterval(refreshUtilities, 15000);
