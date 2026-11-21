(() => {
  const extensionId = "codex-control-center-api-balance";
  const anchorSelector = "[data-ambient-usage-label]";
  const rootAttribute = "data-codex-control-center-api-balance";
  const maxMutationRecords = 256;
  const maxNodesPerMutation = 512;
  const maxAnchorCandidates = 16;
  let payload = null;
  let root = null;
  let anchor = null;
  let observer = null;
  let renderQueued = false;
  let disposed = false;

  const visible = (element) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
  };

  const findVisibleAnchors = () => {
    const candidates = document.querySelectorAll(anchorSelector);
    if (candidates.length > maxAnchorCandidates) return { anchors: [], complete: false };
    const anchors = [];
    for (const candidate of candidates) {
      if (visible(candidate)) anchors.push(candidate);
      if (anchors.length > 1) return { anchors, complete: true };
    }
    return { anchors, complete: true };
  };

  const safePayload = (value) => {
    if (!value || typeof value !== "object" ||
        typeof value.compact !== "string" || value.compact.length > 192 ||
        typeof value.title !== "string" || value.title.length > 96 ||
        !Array.isArray(value.lines) || value.lines.length > 12 ||
        value.lines.some((line) => typeof line !== "string" || line.length > 192 || /[\u0000-\u001f\u007f]/.test(line)) ||
        /[\u0000-\u001f\u007f]/.test(value.compact) || /[\u0000-\u001f\u007f]/.test(value.title)) {
      return null;
    }
    return { compact: value.compact, title: value.title, lines: [...value.lines] };
  };

  const closePanel = () => {
    if (!root) return;
    const button = root.querySelector("button[data-balance-toggle]");
    const panel = root.querySelector("[data-balance-panel]");
    if (button) button.setAttribute("aria-expanded", "false");
    if (panel) panel.hidden = true;
  };

  const removeOwnedNode = () => {
    root?.remove();
    root = null;
    anchor = null;
  };

  const addTextRow = (panel, text) => {
    const row = document.createElement("div");
    row.className = "py-1 text-token-text-primary";
    row.textContent = text;
    panel.append(row);
  };

  const createRoot = () => {
    const container = document.createElement("span");
    container.setAttribute(rootAttribute, "");
    container.className = "relative ml-2 inline-flex min-w-0 shrink-0 items-center";

    const button = document.createElement("button");
    button.type = "button";
    button.dataset.balanceToggle = "";
    button.className = "max-w-full rounded-full border border-token-border-default bg-token-bg-secondary px-2 py-1 text-xs text-token-text-secondary hover:text-token-text-primary focus-visible:outline focus-visible:outline-2";
    button.setAttribute("aria-haspopup", "dialog");
    button.setAttribute("aria-expanded", "false");
    button.addEventListener("click", () => {
      const panel = container.querySelector("[data-balance-panel]");
      if (!panel) return;
      const open = panel.hidden;
      panel.hidden = !open;
      button.setAttribute("aria-expanded", String(open));
    });
    container.append(button);

    const panel = document.createElement("div");
    panel.dataset.balancePanel = "";
    panel.id = "codex-control-center-api-balance-panel";
    panel.className = "absolute bottom-full right-0 z-50 mb-2 w-64 rounded-xl border border-token-border-default bg-token-bg-secondary p-3 text-xs shadow-lg";
    panel.setAttribute("role", "dialog");
    panel.hidden = true;
    button.setAttribute("aria-controls", panel.id);
    container.append(panel);
    return container;
  };

  const renderPayload = () => {
    if (!root || !payload) return;
    const button = root.querySelector("button[data-balance-toggle]");
    const panel = root.querySelector("[data-balance-panel]");
    if (!button || !panel) return;

    button.textContent = payload.compact;
    button.title = payload.title;
    button.setAttribute("aria-label", payload.title + ": " + payload.compact);
    panel.replaceChildren();

    const title = document.createElement("div");
    title.className = "mb-2 border-b border-token-border-default pb-2 text-token-text-secondary";
    title.textContent = payload.title;
    panel.append(title);
    for (const line of payload.lines) addTextRow(panel, line);
  };

  const mount = () => {
    if (disposed) return;
    const { anchors, complete } = findVisibleAnchors();
    if (!complete || anchors.length !== 1 || !payload) {
      removeOwnedNode();
      return;
    }

    const nextAnchor = anchors[0];
    if (!nextAnchor.parentElement) {
      removeOwnedNode();
      return;
    }

    if (!root || anchor !== nextAnchor || root.parentElement !== nextAnchor.parentElement) {
      removeOwnedNode();
      root = createRoot();
      anchor = nextAnchor;
      nextAnchor.parentElement.insertBefore(root, nextAnchor.nextSibling);
    }
    renderPayload();
  };

  const queueMount = () => {
    if (disposed || renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(() => {
      renderQueued = false;
      mount();
    });
  };

  const nodeContainsAnchor = (node) => {
    if (node.nodeType !== Node.ELEMENT_NODE || node.hasAttribute(rootAttribute)) return false;
    if (node.matches(anchorSelector)) return true;
    const walker = document.createTreeWalker(node, NodeFilter.SHOW_ELEMENT);
    for (let visited = 0; visited < maxNodesPerMutation; visited += 1) {
      const current = walker.nextNode();
      if (!current) return false;
      if (current.matches(anchorSelector)) return true;
    }
    return false;
  };

  const handleMutations = (records) => {
    if (disposed) return;
    if (records.length > maxMutationRecords || (payload && (!anchor?.isConnected || !root?.isConnected ||
        !anchor.parentElement || root.parentElement !== anchor.parentElement))) {
      queueMount();
      return;
    }
    for (const record of records) {
      if (record.type === "attributes" &&
          (record.target === anchor || record.target.matches?.(anchorSelector))) {
        queueMount();
        return;
      }
      for (const node of [...record.addedNodes, ...record.removedNodes]) {
        if (nodeContainsAnchor(node)) {
          queueMount();
          return;
        }
      }
    }
  };

  const onPointerDown = (event) => {
    if (root && !root.contains(event.target)) closePanel();
  };
  const onKeyDown = (event) => {
    if (event.key === "Escape") closePanel();
  };

  const startObserver = () => {
    if (observer || !document.body) return;
    observer = new MutationObserver(handleMutations);
    observer.observe(document.body, {
      attributes: true,
      attributeFilter: ["data-ambient-usage-label", "class", "style"],
      childList: true,
      subtree: true,
    });
  };

  const update = (nextPayload) => {
    payload = safePayload(nextPayload);
    if (!payload) removeOwnedNode();
    else queueMount();
  };

  const cleanup = () => {
    if (disposed) return;
    disposed = true;
    observer?.disconnect();
    observer = null;
    document.removeEventListener("pointerdown", onPointerDown, true);
    document.removeEventListener("keydown", onKeyDown, true);
    removeOwnedNode();
  };

  document.addEventListener("pointerdown", onPointerDown, true);
  document.addEventListener("keydown", onKeyDown, true);
  startObserver();
  mount();

  return { update, cleanup };
})()
