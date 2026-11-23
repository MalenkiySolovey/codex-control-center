(() => {
  const factory = /* CODEX_NATIVE_USAGE_FACTORY */ null;
  const railSelector = "[data-app-navigation-rail]";
  const sidebarSelector = "[data-slate-sidebar-content]";
  const rootAttribute = "data-codex-control-center-api-balance-native";
  const leaseMilliseconds = 12000;
  let components, initialization, current, surface, lease;
  let disposed = false;

  const displayText = (value, maximum) => typeof value === "string" && value.trim().length > 0 &&
    value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value);
  const safeArc = (arc) => displayText(arc?.tooltip, 192) &&
    (arc.fill === null || Number.isFinite(arc.fill) && arc.fill >= 0 && arc.fill <= 100);
  const safePayload = (value) => displayText(value?.compact, 192) && displayText(value.title, 96) &&
    safeArc(value.upperArc) && safeArc(value.lowerArc) &&
    Array.isArray(value.rows) && value.rows.length <= 16 && value.rows.every((row) =>
      displayText(row?.label, 192) && (row.value === "" || displayText(row.value, 192)));

  const removeSurface = () => {
    const previous = surface;
    surface = undefined;
    try {
      previous?.root.unmount();
    } finally {
      previous?.container.remove();
    }
  };

  const clear = () => {
    clearTimeout(lease);
    current = undefined;
    removeSurface();
  };

  const isVisible = (element) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
  };

  const hostFiber = (element) => {
    const keys = Object.getOwnPropertyNames(element).filter((key) => key.startsWith("__reactFiber$"));
    return keys.length === 1 ? element[keys[0]] : null;
  };

  const isProfileButton = (button) => {
    let node = hostFiber(button);
    for (let depth = 0; node && depth < 32; depth++, node = node.return) {
      if (typeof node.type !== "function") continue;
      const source = Function.prototype.toString.call(node.type);
      if (source.includes("codex.profileFooter.openProfileMenu") && source.includes("codex.profileFooter.openSettings")) return true;
    }
    return false;
  };

  const findProfileAnchor = () => {
    for (const selector of [railSelector, sidebarSelector]) {
      const sidebars = [...document.querySelectorAll(selector)].filter(isVisible);
      if (sidebars.length !== 1) continue;
      const sidebar = sidebars[0];
      const buttons = sidebar.querySelectorAll(".sidebar-item button[aria-label]");
      if (buttons.length > 32) continue;
      const profiles = [...buttons].filter((button) => isVisible(button) && isProfileButton(button));
      if (profiles.length !== 1) continue;

      // The rail stacks the native controls above the final profile slot.
      // The wider sidebar uses a single profile row in its vertical footer.
      const button = profiles[0];
      const slot = button.closest(".sidebar-item").parentElement;
      const row = slot?.parentElement;
      if (!row || !sidebar.contains(row)) continue;
      const vertical = getComputedStyle(row).flexDirection === "column";
      const anchor = vertical ? slot : row.parentElement;
      if (!anchor?.parentElement || !sidebar.contains(anchor) || anchor === sidebar ||
          getComputedStyle(anchor.parentElement).flexDirection !== "column") continue;

      return { anchor, button, vertical };
    }
    return null;
  };

  const withNativeContext = (anchor, child) => {
    const { AppScope, React } = components;
    if (typeof AppScope?.id !== "symbol") return null;

    const intlContext = window.__REACT_INTL_CONTEXT__;
    let app, intl;
    // Read only the version-matched presentation providers from the footer's ancestry.
    // Values stay inside the renderer and reach the original components through React.
    let node = hostFiber(anchor);
    for (let depth = 0; node && depth < 256; depth++, node = node.return) {
      const context = node.type;
      if (context?.$$typeof !== Symbol.for("react.context")) continue;
      const value = node.memoizedProps?.value;
      if (!app && context.displayName === AppScope.__scopeBrand && value instanceof Map && value.has(AppScope.id)) {
        app = { context, value };
      }
      if (!intl && context === intlContext && value) intl = { context, value };
    }

    if (!app) return null;
    if (intl) child = React.createElement(intl.context, { value: intl.value }, child);
    return React.createElement(app.context, { value: app.value }, child);
  };

  const ProviderUsage = ({ payload, container, profile }) => {
    const { React, Ring, Glyph, Popup, Menu, Row, Trigger, Tooltip } = components;
    const { createElement: h } = React;
    const [open, setOpen] = React.useState(false);
    React.useLayoutEffect(() => {
      const button = container.querySelector("button");
      const aboveProfile = container.nextElementSibling === profile.anchor && button &&
        button.getBoundingClientRect().bottom <= profile.button.getBoundingClientRect().top + 1;
      container.toggleAttribute(rootAttribute, !!aboveProfile && container.querySelectorAll("path[pathLength='100']").length === 2);
    }, [container, profile]);

    const ring = h(Ring, {
      primary: payload.upperArc.fill ?? 0,
      secondary: payload.lowerArc.fill ?? 0,
      primaryTooltip: payload.upperArc.tooltip,
      secondaryTooltip: payload.lowerArc.tooltip,
      interactive: !open,
      children: h(Glyph, { kind: "included" }),
    });
    const trigger = h(Trigger, { "aria-label": payload.compact, children: ring });
    const rows = payload.rows.map(({ label, value }, index) => Row({ label, value: { text: value } }, index));
    return h(Tooltip.Provider, { delayDuration: 350, skipDelayDuration: 0 }, h(Popup, {
      align: "end",
      contentWidth: "menuReadout",
      size: "compact",
      side: "right",
      open,
      onOpenChange: setOpen,
      triggerButton: trigger,
    },
    h(Menu.Title, { wrap: true }, payload.title),
    h(Menu.Separator),
    h(Menu.Section, {}, rows)));
  };

  const render = () => {
    if (disposed || !components || !current || location.href !== "app://-/index.html") return;
    const profile = findProfileAnchor();
    if (!profile || document.querySelector("[data-ambient-usage-label]")) {
      removeSurface();
      return;
    }

    if (surface?.anchor !== profile.anchor || !surface.container.isConnected) {
      removeSurface();
      const container = document.createElement("div");
      container.style.display = "inline-flex";
      if (!profile.vertical) container.className = "self-start px-row-x";
      container.style.setProperty("-webkit-app-region", "no-drag");
      profile.anchor.before(container);
      const root = components.createRoot(container, {
        onUncaughtError: () => queueMicrotask(() => {
          if (surface?.container === container) clear();
        }),
      });
      surface = { anchor: profile.anchor, container, root };
    } else if (surface.container.nextElementSibling !== profile.anchor) {
      profile.anchor.before(surface.container);
    }

    const { container, root } = surface;
    const view = withNativeContext(profile.button, components.React.createElement(ProviderUsage, { payload: current, container, profile }));
    if (view) root.render(view);
    else removeSurface();
  };

  const loadComponents = async () => {
    const entry = "/assets/348410.35ea11b6a0.js";
    const scripts = [...document.scripts].filter((script) =>
      script.type === "module" && script.getAttribute("src") === entry);
    if (scripts.length !== 1 || typeof factory !== "function") return;

    const { __webpack_require__: require } = await import(new URL(entry, location.href).href);
    if (typeof require !== "function" || typeof require.e !== "function") return;
    await require.e(212106);
    if (disposed || typeof require("w5W4").AmbientUsageRailMenu !== "function") return;

    const usage = {};
    factory({ exports: usage }, usage, require);
    const React = require("zd8w");
    const { createRoot } = require("CE87");
    if (typeof usage.Ring !== "function" || typeof usage.Row !== "function" ||
        typeof usage.Popup !== "function" || typeof createRoot !== "function") return;
    components = { ...usage, React, createRoot, AppScope: require("OWtl").u };
    render();
  };

  return {
    update(payload) {
      if (disposed || !safePayload(payload)) {
        clear();
        return;
      }

      current = payload;
      clearTimeout(lease);
      lease = setTimeout(clear, leaseMilliseconds);
      if (components) render();
      else initialization ||= loadComponents().catch(clear);
    },
    cleanup() {
      disposed = true;
      clear();
    },
  };
})()
