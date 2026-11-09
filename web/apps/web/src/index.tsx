import { render } from "@solidjs/web";
import App from "./App";
import { loadBranding } from "./branding";
import "./styles.css";

const root = document.getElementById("app");
if (!root) throw new Error("Missing #app");
void loadBranding();
render(() => <App />, root);
