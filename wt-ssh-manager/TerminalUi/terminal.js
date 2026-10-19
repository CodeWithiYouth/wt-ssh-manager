"use strict";

const bridge = window.chrome && window.chrome.webview;

function post(message) {
  if (bridge) {
    bridge.postMessage(message);
  }
}

window.addEventListener("error", event => {
  post({ type: "renderer-error", message: event.message || "script error" });
});
window.addEventListener("unhandledrejection", event => {
  post({ type: "renderer-error", message: String(event.reason) });
});

const terminal = new window.Terminal({
  allowProposedApi: false,
  convertEol: false,
  cursorBlink: true,
  fontFamily: 'Consolas, "Cascadia Mono", monospace',
  fontSize: 14,
  scrollback: 10000,
  theme: {
    background: "#0c0c0c",
    foreground: "#f3f3f3",
    cursor: "#f3f3f3",
    selectionBackground: "#3a5877",
    black: "#0c0c0c",
    red: "#c50f1f",
    green: "#13a10e",
    yellow: "#c19c00",
    blue: "#0037da",
    magenta: "#881798",
    cyan: "#3a96dd",
    white: "#cccccc",
    brightBlack: "#767676",
    brightRed: "#e74856",
    brightGreen: "#16c60c",
    brightYellow: "#f9f1a5",
    brightBlue: "#3b78ff",
    brightMagenta: "#b4009e",
    brightCyan: "#61d6d6",
    brightWhite: "#f2f2f2"
  }
});

const fitAddon = new window.FitAddon.FitAddon();
terminal.loadAddon(fitAddon);
terminal.open(document.getElementById("terminal"));

let refreshPending = false;
let clearTexturePending = false;

function refreshTerminalView(clearTexture = false) {
  if (clearTexture && typeof terminal.clearTextureAtlas === "function") {
    terminal.clearTextureAtlas();
  }

  if (terminal.rows > 0) {
    terminal.refresh(0, terminal.rows - 1);
  }
}

function scheduleTerminalRefresh(clearTexture = false) {
  clearTexturePending ||= clearTexture;
  if (refreshPending) {
    return;
  }

  refreshPending = true;
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      refreshPending = false;
      const shouldClearTexture = clearTexturePending;
      clearTexturePending = false;
      refreshTerminalView(shouldClearTexture);
    });
  });
}

function fitAndReport() {
  fitAddon.fit();
  scheduleTerminalRefresh(true);
  post({ type: "resize", cols: terminal.cols, rows: terminal.rows });
}

terminal.onData(data => post({ type: "input", data }));

if (bridge) {
  bridge.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "output") {
      terminal.write(message.data, () => scheduleTerminalRefresh());
    } else if (message.type === "selection") {
      post({ type: "selection", data: terminal.getSelection() });
    } else if (message.type === "focus") {
      terminal.focus();
    } else if (message.type === "fit") {
      requestAnimationFrame(fitAndReport);
    }
  });
}

document.addEventListener("keydown", event => {
  if (!event.ctrlKey || !event.shiftKey) {
    return;
  }

  const key = event.key.toLowerCase();
  if (key === "c" || key === "v") {
    event.preventDefault();
    post({ type: key === "c" ? "copy" : "paste" });
  }
}, true);

const terminalShell = document.getElementById("terminal-shell");
new ResizeObserver(() => requestAnimationFrame(fitAndReport))
  .observe(terminalShell);

function refreshVisibleTerminal() {
  fitAndReport();
  terminal.focus();
}

window.addEventListener("focus", refreshVisibleTerminal);
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    refreshVisibleTerminal();
  }
});
window.focusTerminal = () => terminal.focus();

const nextFrame = () => new Promise(resolve => requestAnimationFrame(resolve));

async function initializeTerminal() {
  await nextFrame();
  await nextFrame();
  fitAndReport();
  post({ type: "ready" });
  terminal.focus();
  setTimeout(refreshVisibleTerminal, 50);
  setTimeout(refreshVisibleTerminal, 250);
}

initializeTerminal();

