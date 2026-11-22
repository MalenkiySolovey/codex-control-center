(() => {
  const nativeAnchor = "[data-ambient-usage-label]";
  const toolbarAnchor = "[data-app-shell-header-toolbar]";
  const rootAttribute = "data-codex-control-center-api-balance";
  const leaseMilliseconds = 12000;
  const listeners = new AbortController();
  let surface = null;
  let lastPayload = null;
  let lease = null;

  const displayText = (text, maximum) =>
    typeof text === "string" && text.trim().length > 0 && text.length <= maximum &&
    !/[\u0000-\u001f\u007f]/.test(text);

  const isPayload = (value) =>
    displayText(value?.compact, 192) && displayText(value?.title, 96) &&
    Array.isArray(value.lines) && value.lines.length <= 12 && value.lines.every((line) => displayText(line, 192));

  const uniqueVisible = (selector) => {
    const candidates = document.querySelectorAll(selector);
    if (candidates.length > 16) return null;
    const visible = [...candidates].filter((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    });
    return visible.length === 1 ? visible[0] : null;
  };

  const removeSurface = () => {
    surface?.panel.remove();
    surface?.root.remove();
    surface = null;
    lastPayload = null;
  };

  const closePanel = () => {
    if (!surface || surface.panel.hidden) return;
    surface.panel.hidePopover?.();
    surface.panel.hidden = true;
    surface.button.setAttribute("aria-expanded", "false");
  };

  const positionPanel = () => {
    if (!surface || surface.panel.hidden) return;
    const { button, panel, preferBelow } = surface;
    const rect = button.getBoundingClientRect();
    const gutter = 12;
    const gap = 8;
    const width = Math.min(256, window.innerWidth - 2 * gutter);
    if (width <= 0 || rect.width <= 0 || rect.height <= 0) {
      closePanel();
      return;
    }

    panel.style.width = width + "px";
    const below = Math.max(0, window.innerHeight - rect.bottom - gap - gutter);
    const above = Math.max(0, rect.top - gap - gutter);
    const height = panel.scrollHeight + panel.offsetHeight - panel.clientHeight;
    const preferred = preferBelow ? below : above;
    const opposite = preferBelow ? above : below;
    const down = preferred >= height || preferred >= opposite ? preferBelow : !preferBelow;
    const verticalSpace = down ? below : above;
    if (verticalSpace <= 0) {
      closePanel();
      return;
    }

    panel.style.maxHeight = verticalSpace + "px";
    const left = Math.min(Math.max(gutter, rect.left), window.innerWidth - width - gutter);
    const top = down ? rect.bottom + gap : rect.top - gap - panel.offsetHeight;
    panel.style.left = left + "px";
    panel.style.top = top + "px";
  };

  const createSurface = (anchor, preferBelow) => {
    const root = document.createElement("span");
    root.setAttribute(rootAttribute, "");
    root.className = "ml-2 inline-flex min-w-0 shrink-0 items-center pointer-events-auto";
    root.style.setProperty("-webkit-app-region", "no-drag");

    const button = document.createElement("button");
    button.type = "button";
    button.dataset.balanceToggle = "";
    button.className = "truncate rounded-full border border-token-border-default bg-token-bg-secondary px-2 py-1 text-xs text-token-text-secondary hover:text-token-text-primary focus-visible:outline focus-visible:outline-2 pointer-events-auto";
    button.style.maxWidth = "min(20rem, 45vw)";
    button.style.setProperty("-webkit-app-region", "no-drag");
    button.setAttribute("aria-haspopup", "dialog");
    button.setAttribute("aria-expanded", "false");

    const panel = document.createElement("div");
    panel.id = "codex-control-center-api-balance-panel";
    panel.dataset.balancePanel = "";
    panel.className = "fixed z-50 box-border rounded-xl border border-token-border-default bg-token-bg-secondary p-3 text-xs text-token-text-primary shadow-lg pointer-events-auto";
    panel.style.position = "fixed";
    panel.style.zIndex = "50";
    panel.style.boxSizing = "border-box";
    panel.style.inset = "auto";
    panel.style.margin = "0";
    panel.style.overflowY = "auto";
    panel.style.overflowWrap = "anywhere";
    panel.style.setProperty("-webkit-app-region", "no-drag");
    panel.setAttribute("role", "dialog");
    if (typeof panel.showPopover === "function") panel.setAttribute("popover", "manual");
    panel.hidden = true;
    button.setAttribute("aria-controls", panel.id);

    button.addEventListener("click", () => {
      if (!panel.hidden) {
        closePanel();
        return;
      }

      panel.hidden = false;
      panel.showPopover?.();
      button.setAttribute("aria-expanded", "true");
      positionPanel();
    });
    root.append(button);
    anchor.after(root);
    document.body.append(panel);
    return { root, button, panel, anchor, preferBelow };
  };

  const render = (value) => {
    const { button, panel } = surface;
    button.textContent = value.compact;
    button.title = value.title;
    button.setAttribute("aria-label", value.title + ": " + value.compact);
    panel.setAttribute("aria-label", value.title);
    const title = document.createElement("div");
    title.className = "mb-2 border-b border-token-border-default pb-2 text-token-text-secondary";
    title.textContent = value.title;
    const rows = value.lines.map((text) => {
      const row = document.createElement("div");
      row.className = "py-1";
      row.textContent = text;
      return row;
    });
    panel.replaceChildren(title, ...rows);
    positionPanel();
  };

  const update = (value) => {
    if (listeners.signal.aborted) return;
    clearTimeout(lease);
    const native = uniqueVisible(nativeAnchor);
    const anchor = native || uniqueVisible(toolbarAnchor);
    if (!anchor || !isPayload(value)) {
      removeSurface();
      return;
    }

    if (!surface || surface.anchor !== anchor || !surface.root.isConnected || !surface.panel.isConnected) {
      removeSurface();
      surface = createSurface(anchor, !native);
    } else if (anchor.nextElementSibling !== surface.root) {
      anchor.after(surface.root);
    }

    const content = JSON.stringify(value);
    if (content !== lastPayload) {
      render(value);
      lastPayload = content;
    }
    lease = setTimeout(removeSurface, leaseMilliseconds);
  };

  const capture = { capture: true, signal: listeners.signal };
  document.addEventListener("pointerdown", (event) => {
    if (surface && !surface.root.contains(event.target) && !surface.panel.contains(event.target)) closePanel();
  }, capture);
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closePanel();
  }, capture);
  window.addEventListener("resize", positionPanel, { signal: listeners.signal });
  window.addEventListener("scroll", positionPanel, capture);

  const cleanup = () => {
    listeners.abort();
    clearTimeout(lease);
    removeSurface();
  };

  return { update, cleanup };
})()
