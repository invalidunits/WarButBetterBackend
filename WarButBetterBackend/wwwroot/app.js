(() => {
  const createBtn = document.getElementById("createMatchBtn");
  const queueBtn = document.getElementById("queueMatchBtn");
  const joinBtn = document.getElementById("joinMatchBtn");
  const copyBtn = document.getElementById("copyLinkBtn");
  const shareBtn = document.getElementById("nativeShareBtn");
  const matchIdInput = document.getElementById("matchIdInput");
  const inviteLinkInput = document.getElementById("inviteLinkInput");
  const shareSection = document.getElementById("shareSection");
  const statusBadge = document.getElementById("statusBadge");
  const statusText = document.getElementById("statusText");
  const gameFrame = document.getElementById("gameFrame");

  let currentMatchId = null;
  let statusPollHandle = null;

  function buildGameIndexUrl() {
    // Resolve relative to the current page so this still works when hosted behind a path prefix.
    return new URL("game/index.html", window.location.href);
  }

  function setStatus(kind, text) {
    statusBadge.className = "status-badge";
    switch (kind) {
      case "waiting":
        statusBadge.classList.add("status-waiting");
        statusBadge.textContent = "Waiting";
        break;
      case "running":
        statusBadge.classList.add("status-running");
        statusBadge.textContent = "Running";
        break;
      case "full":
        statusBadge.classList.add("status-full");
        statusBadge.textContent = "Full";
        break;
      case "error":
        statusBadge.classList.add("status-error");
        statusBadge.textContent = "Error";
        break;
      default:
        statusBadge.classList.add("status-idle");
        statusBadge.textContent = "Idle";
        break;
    }

    statusText.textContent = text;
  }

  function validGuid(value) {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
  }

  function buildInviteLink(matchId) {
    const url = new URL(window.location.href);
    url.searchParams.set("match", matchId);
    return url.toString();
  }

  function setCurrentMatch(matchId) {
    currentMatchId = matchId;
    matchIdInput.value = matchId;
    const invite = buildInviteLink(matchId);
    inviteLinkInput.value = invite;
    shareSection.classList.remove("hidden");

    const browserUrl = new URL(window.location.href);
    browserUrl.searchParams.set("match", matchId);
    window.history.replaceState({}, "", browserUrl);
  }

  function clearCurrentMatch() {
    currentMatchId = null;
    matchIdInput.value = "";
    inviteLinkInput.value = "";
    shareSection.classList.add("hidden");

    const browserUrl = new URL(window.location.href);
    browserUrl.searchParams.delete("match");
    window.history.replaceState({}, "", browserUrl);
  }

  async function callJson(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
      throw new Error(`Request failed (${response.status})`);
    }

    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  async function loadEmbeddedGame(matchId, queueMode = false) {
    const backendUrl = `${window.location.protocol}//${window.location.host}`;
    const src = buildGameIndexUrl();
    if (matchId) {
      src.searchParams.set("match", matchId);
    } else {
      src.searchParams.delete("match");
    }
    if (queueMode) {
      src.searchParams.set("queue", "1");
    } else {
      src.searchParams.delete("queue");
    }
    src.searchParams.set("backend", backendUrl);

    gameFrame.src = src.toString();
    gameFrame.classList.remove("hidden");
  }

  async function refreshMatchStatus(matchId) {
    try {
      const data = await callJson(`/match/${matchId}/status`, { method: "GET" });
      if (!data) {
        setStatus("error", "Unable to read match status.");
        return;
      }

      if (data.state === "WaitingForPlayers") {
        setStatus("waiting", `Waiting for players (${data.connectedPlayers}/${data.maxPlayers}).`);
      } else if (data.state === "StartingGame" || data.state === "RunningGame") {
        setStatus("running", `Game state: ${data.state}.`);
      } else if (data.canJoinAsPlayer === false) {
        setStatus("full", "Match is full.");
      } else {
        setStatus("idle", "Match is available.");
      }
    } catch {
      setStatus("error", "Match not found or expired.");
    }
  }

  function startStatusPolling(matchId) {
    if (statusPollHandle !== null) {
      window.clearInterval(statusPollHandle);
    }

    refreshMatchStatus(matchId);
    statusPollHandle = window.setInterval(() => {
      refreshMatchStatus(matchId);
    }, 2000);
  }

  async function createPrivateMatch() {
    setStatus("idle", "Creating match...");
    try {
      const matchId = await callJson("/match", { method: "POST" });
      if (!matchId || !validGuid(String(matchId))) {
        throw new Error("Invalid match response");
      }

      const id = String(matchId);
      setCurrentMatch(id);
      await loadEmbeddedGame(id);
      startStatusPolling(id);
      setStatus("waiting", "Match created. Share the link and wait for another player.");
    } catch (error) {
      setStatus("error", `Failed to create match: ${error.message}`);
    }
  }

  async function queueForMatch() {
    setStatus("idle", "Opening game queue...");
    try {
      if (statusPollHandle !== null) {
        window.clearInterval(statusPollHandle);
        statusPollHandle = null;
      }

      clearCurrentMatch();
      await loadEmbeddedGame(null, true);
      setStatus("waiting", "Game opened in queue mode. Waiting now happens in-game.");
    } catch (error) {
      setStatus("error", `Queue failed: ${error.message}`);
    }
  }

  async function joinMatchFromInput() {
    const value = matchIdInput.value.trim();
    if (!validGuid(value)) {
      setStatus("error", "Enter a valid match ID.");
      return;
    }

    setCurrentMatch(value);
    await loadEmbeddedGame(value);
    startStatusPolling(value);
    setStatus("waiting", "Joining match...");
  }

  async function copyInviteLink() {
    const invite = inviteLinkInput.value;
    if (!invite) {
      return;
    }

    try {
      await navigator.clipboard.writeText(invite);
      setStatus("idle", "Invite link copied.");
    } catch {
      setStatus("error", "Copy failed. Select and copy manually.");
    }
  }

  async function shareInviteLink() {
    if (!currentMatchId) {
      setStatus("error", "Create or join a match first.");
      return;
    }

    const invite = buildInviteLink(currentMatchId);
    if (!navigator.share) {
      setStatus("error", "Native share not available in this browser.");
      return;
    }

    try {
      await navigator.share({
        title: "War But Better Match",
        text: "Join my War But Better match",
        url: invite,
      });
      setStatus("idle", "Invite shared.");
    } catch {
      // User canceled or share failed; keep UI unchanged.
    }
  }

  function initFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const fromUrl = params.get("match");
    if (!fromUrl) {
      return;
    }

    if (!validGuid(fromUrl)) {
      setStatus("error", "Invalid match id in URL.");
      return;
    }

    setCurrentMatch(fromUrl);
    loadEmbeddedGame(fromUrl);
    startStatusPolling(fromUrl);
    setStatus("waiting", "Invite link opened. Attempting auto-join...");
  }

  createBtn.addEventListener("click", createPrivateMatch);
  queueBtn.addEventListener("click", queueForMatch);
  joinBtn.addEventListener("click", joinMatchFromInput);
  copyBtn.addEventListener("click", copyInviteLink);
  shareBtn.addEventListener("click", shareInviteLink);

  setStatus("idle", "Create a private match or queue for a random one.");
  initFromUrl();
})();
