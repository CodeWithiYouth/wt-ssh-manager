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

function fitAndReport() {
  fitAddon.fit();
  post({ type: "resize", cols: terminal.cols, rows: terminal.rows });
}

terminal.onData(data => post({ type: "input", data }));

if (bridge) {
  bridge.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "output") {
      terminal.write(message.data);
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

new ResizeObserver(() => requestAnimationFrame(fitAndReport))
  .observe(document.getElementById("terminal-container"));

window.addEventListener("focus", () => terminal.focus());
window.focusTerminal = () => terminal.focus();

requestAnimationFrame(() => {
  fitAndReport();
  post({ type: "ready" });
  terminal.focus();
});

