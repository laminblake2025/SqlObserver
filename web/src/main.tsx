import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./App";
import { TimeDisplayProvider } from "./TimeDisplayContext";
import "./styles.css";

const rootElement = document.getElementById("root");

if (rootElement === null) {
  throw new Error("SqlObserver could not find its root element.");
}

createRoot(rootElement).render(
  <StrictMode>
    <TimeDisplayProvider><App /></TimeDisplayProvider>
  </StrictMode>,
);
