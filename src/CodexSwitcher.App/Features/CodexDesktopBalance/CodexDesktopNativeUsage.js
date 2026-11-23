(() => {
  const factory = /* CODEX_NATIVE_USAGE_FACTORY */ null;
  const toolbarSelector = "[data-app-shell-header-toolbar]";
  const rootAttribute = "data-codex-control-center-api-balance-native";
  const leaseMilliseconds = 12000;
  let components, initialization, current, surface, lease;
  let disposed = false;

  const displayText = (value, maximum) => typeof value === "string" && value.trim().length > 0 &&
    value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value);
  const safePayload = (value) => displayText(value?.compact, 192) && displayText(value.title, 96) &&
    (value.percent === null || Number.isFinite(value.percent) && value.percent >= 0 && value.percent <= 100) &&
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

  const findToolbar = () => {
    const candidates = document.querySelectorAll(toolbarSelector);
    if (candidates.length > 16) return null;
    const visible = [...candidates].filter((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    });
    return visible.length === 1 ? visible[0] : null;
  };

  const withNativeContext = (toolbar, child) => {
    const { AppScope, React } = components;
    const fiberKeys = Object.getOwnPropertyNames(toolbar).filter((key) => key.startsWith("__reactFiber$"));
    if (fiberKeys.length !== 1 || typeof AppScope?.id !== "symbol") return null;

    const intlContext = window.__REACT_INTL_CONTEXT__;
    let app, intl;
    // Read only the version-matched presentation providers from this toolbar's ancestry.
    // Values stay inside the renderer and reach the original components through React.
    let node = toolbar[fiberKeys[0]];
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

  const ProviderUsage = ({ payload, container }) => {
    const { React, Ring, Glyph, Indicator, Popup, Menu, Row, Trigger, Tooltip } = components;
    const { createElement: h } = React;
    const [open, setOpen] = React.useState(false);
    React.useLayoutEffect(() => {
      const meter = payload.percent === null ? "[data-transition-phase]" : "path[pathLength='100']";
      container.toggleAttribute(rootAttribute, !!container.querySelector("button") && !!container.querySelector(meter));
    }, [container, payload.percent]);

    const ring = payload.percent === null
      ? h(Indicator, { source: null, interactive: false })
      : h(Ring, { primary: payload.percent, interactive: false, children: h(Glyph, { kind: "included" }) });
    const trigger = h(Tooltip, {
      content: payload.compact,
      disabled: open,
      side: "bottom",
      children: h(Trigger, { "aria-label": payload.compact, children: ring }),
    });
    const rows = payload.rows.map(({ label, value }, index) => Row({ label, value: { text: value } }, index));
    return h(Tooltip.Provider, { delayDuration: 350, skipDelayDuration: 0 }, h(Popup, {
      align: "end",
      contentWidth: "menuReadout",
      size: "compact",
      side: "bottom",
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
    const toolbar = findToolbar();
    if (!toolbar || document.querySelector("[data-ambient-usage-label]")) {
      removeSurface();
      return;
    }

    if (surface?.toolbar !== toolbar || !surface.container.isConnected) {
      removeSurface();
      const container = document.createElement("div");
      container.style.display = "inline-flex";
      container.style.setProperty("-webkit-app-region", "no-drag");
      toolbar.after(container);
      const root = components.createRoot(container, {
        onUncaughtError: () => queueMicrotask(() => {
          if (surface?.container === container) clear();
        }),
      });
      surface = { toolbar, container, root };
    } else if (toolbar.nextElementSibling !== surface.container) {
      toolbar.after(surface.container);
    }

    const { container, root } = surface;
    const view = withNativeContext(toolbar, components.React.createElement(ProviderUsage, { payload: current, container }));
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
